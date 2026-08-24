#include "DNSHandler.h"

#include "SocksHelper.h"

#include <atomic>
#include <memory>
#include <stop_token>

extern bool dnsProx;
extern string dnsHost;
extern USHORT dnsPort;

namespace
{
	constexpr auto DNS_TIMEOUT = chrono::seconds(4);
	constexpr long DNS_POLL_MICROSECONDS = 100000;

	class SocketHandle
	{
	public:
		explicit SocketHandle(SOCKET value = INVALID_SOCKET) noexcept : value(value)
		{
		}

		~SocketHandle()
		{
			if (value != INVALID_SOCKET)
				closesocket(value);
		}

		SocketHandle(const SocketHandle&) = delete;
		SocketHandle& operator=(const SocketHandle&) = delete;

		SOCKET get() const noexcept
		{
			return value;
		}

	private:
		SOCKET value;
	};

	class DNSJob
	{
	public:
		DNSJob(ENDPOINT_ID id, const SOCKADDR_IN6& target, const char* packet, int length,
			PNF_UDP_OPTIONS options, const SOCKADDR_IN6& server, bool proxy) :
			id(id),
			target(target),
			packet(packet, packet + length),
			option(GetOptionSize(options)),
			server(server),
			proxy(proxy)
		{
			memcpy(option.data(), options, option.size());
		}

		~DNSJob()
		{
			RequestStop();
			Join();
		}

		DNSJob(const DNSJob&) = delete;
		DNSJob& operator=(const DNSJob&) = delete;

		void Start()
		{
			worker = jthread([this](stop_token token) noexcept {
				try
				{
					if (proxy)
					{
						HandleRemote(token);
						remote.Stop();
					}
					else
					{
						HandleDirect(token);
					}
				}
				catch (...)
				{
					if (proxy)
						remote.Stop();
				}

				finished.store(true, memory_order_release);
			});
		}

		void RequestStop() noexcept
		{
			cancelled.store(true, memory_order_release);
			if (worker.joinable())
				worker.request_stop();

			// Stop is idempotent and also releases a worker blocked in SOCKS I/O.
			if (proxy)
				remote.Stop();
		}

		void Join() noexcept
		{
			if (worker.joinable() && worker.get_id() != this_thread::get_id())
				worker.join();
		}

		bool Finished() const noexcept
		{
			return finished.load(memory_order_acquire);
		}

	private:
		static size_t GetOptionSize(PNF_UDP_OPTIONS options)
		{
			return sizeof(NF_UDP_OPTIONS) +
				(options->optionsLength > 0 ? static_cast<size_t>(options->optionsLength) - 1 : 0);
		}

		bool IsCancelled(stop_token token) const noexcept
		{
			return token.stop_requested() || cancelled.load(memory_order_acquire);
		}

		void PostReceive(const char* buffer, int length, stop_token token)
		{
			if (length > 0 && !IsCancelled(token))
				nf_udpPostReceive(id, reinterpret_cast<PBYTE>(&target), buffer, length,
					reinterpret_cast<PNF_UDP_OPTIONS>(option.data()));
		}

		void HandleDirect(stop_token token)
		{
			if (IsCancelled(token))
				return;

			SocketHandle remoteSocket(socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP));
			if (remoteSocket.get() == INVALID_SOCKET)
				return;

			int v6only = 0;
			if (setsockopt(remoteSocket.get(), IPPROTO_IPV6, IPV6_V6ONLY,
				reinterpret_cast<char*>(&v6only), sizeof(v6only)) == SOCKET_ERROR)
				return;

			SOCKADDR_IN6 addr{};
			IN6ADDR_SETANY(&addr);
			if (bind(remoteSocket.get(), reinterpret_cast<PSOCKADDR>(&addr), sizeof(addr)) == SOCKET_ERROR)
				return;

			if (IsCancelled(token))
				return;

			const int serverLength = server.sin6_family == AF_INET ? sizeof(SOCKADDR_IN) : sizeof(SOCKADDR_IN6);
			if (sendto(remoteSocket.get(), packet.data(), static_cast<int>(packet.size()), 0,
				reinterpret_cast<const SOCKADDR*>(&server), serverLength) != static_cast<int>(packet.size()))
				return;

			vector<char> buffer(65536);
			const auto deadline = chrono::steady_clock::now() + DNS_TIMEOUT;
			while (!IsCancelled(token) && chrono::steady_clock::now() < deadline)
			{
				timeval timeout{};
				timeout.tv_usec = DNS_POLL_MICROSECONDS;

				fd_set fds;
				FD_ZERO(&fds);
				FD_SET(remoteSocket.get(), &fds);

				const int ready = select(0, &fds, nullptr, nullptr, &timeout);
				if (ready == SOCKET_ERROR)
					return;
				if (ready == 0)
					continue;
				if (IsCancelled(token))
					return;

				const int size = recvfrom(remoteSocket.get(), buffer.data(), static_cast<int>(buffer.size()), 0, nullptr, nullptr);
				if (size == SOCKET_ERROR || size == 0)
					return;

				PostReceive(buffer.data(), size, token);
				return;
			}
		}

		void HandleRemote(stop_token token)
		{
			if (IsCancelled(token) || !remote.Associate())
				return;
			if (IsCancelled(token) || !remote.CreateUDP())
				return;
			if (IsCancelled(token) || remote.Send(&server, packet.data(), static_cast<int>(packet.size())) != static_cast<int>(packet.size()))
				return;

			vector<char> buffer(65536);
			const auto deadline = chrono::steady_clock::now() + DNS_TIMEOUT;
			while (!IsCancelled(token) && chrono::steady_clock::now() < deadline)
			{
				timeval timeout{};
				timeout.tv_usec = DNS_POLL_MICROSECONDS;

				const int size = remote.Read(nullptr, buffer.data(), static_cast<int>(buffer.size()), &timeout);
				if (size == SOCKET_ERROR)
					return;
				if (size == 0)
					continue;

				PostReceive(buffer.data(), size, token);
				return;
			}
		}

		ENDPOINT_ID id;
		SOCKADDR_IN6 target{};
		vector<char> packet;
		vector<unsigned char> option;
		SOCKADDR_IN6 server{};
		bool proxy;
		atomic_bool cancelled{ false };
		atomic_bool finished{ false };
		SocksHelper::UDP remote;
		jthread worker;
	};

	mutex dnsJobsLock;
	multimap<ENDPOINT_ID, shared_ptr<DNSJob>> dnsJobs;
	set<ENDPOINT_ID> activeEndpoints;
	SOCKADDR_IN6 dnsAddr{};
	bool acceptingJobs = false;

	void StopAndJoin(vector<shared_ptr<DNSJob>>& jobs)
	{
		for (const auto& job : jobs)
			job->RequestStop();
		for (const auto& job : jobs)
			job->Join();
	}

	void ReapFinished()
	{
		vector<shared_ptr<DNSJob>> finishedJobs;
		{
			lock_guard<mutex> lock(dnsJobsLock);
			for (auto job = dnsJobs.begin(); job != dnsJobs.end();)
			{
				if (job->second->Finished())
				{
					finishedJobs.emplace_back(move(job->second));
					job = dnsJobs.erase(job);
				}
				else
				{
					++job;
				}
			}
		}

		for (const auto& job : finishedJobs)
			job->Join();
	}
}

bool DNSHandler::INIT()
{
	SOCKADDR_IN6 address{};
	auto ipv4 = reinterpret_cast<PSOCKADDR_IN>(&address);
	if (inet_pton(AF_INET, dnsHost.c_str(), &ipv4->sin_addr) == 1)
	{
		ipv4->sin_family = AF_INET;
		ipv4->sin_port = htons(dnsPort);
	}
	else
	{
		auto ipv6 = reinterpret_cast<PSOCKADDR_IN6>(&address);
		if (inet_pton(AF_INET6, dnsHost.c_str(), &ipv6->sin6_addr) != 1)
		{
			FREE();
			return false;
		}

		ipv6->sin6_family = AF_INET6;
		ipv6->sin6_port = htons(dnsPort);
	}

	FREE();
	lock_guard<mutex> lock(dnsJobsLock);
	dnsAddr = address;
	acceptingJobs = true;
	return true;
}

void DNSHandler::FREE()
{
	vector<shared_ptr<DNSJob>> jobs;
	{
		lock_guard<mutex> lock(dnsJobsLock);
		acceptingJobs = false;
		jobs.reserve(dnsJobs.size());
		for (auto& job : dnsJobs)
			jobs.emplace_back(move(job.second));
		dnsJobs.clear();
		activeEndpoints.clear();
	}

	StopAndJoin(jobs);
}

void DNSHandler::Activate(ENDPOINT_ID id)
{
	lock_guard<mutex> lock(dnsJobsLock);
	if (acceptingJobs)
		activeEndpoints.insert(id);
}

void DNSHandler::Cancel(ENDPOINT_ID id)
{
	vector<shared_ptr<DNSJob>> jobs;
	{
		lock_guard<mutex> lock(dnsJobsLock);
		activeEndpoints.erase(id);
		auto [first, last] = dnsJobs.equal_range(id);
		for (auto job = first; job != last; ++job)
			jobs.emplace_back(move(job->second));
		dnsJobs.erase(first, last);
	}

	StopAndJoin(jobs);
}

bool DNSHandler::IsDNS(PSOCKADDR_IN6 target)
{
	if (target->sin6_family == AF_INET)
		return reinterpret_cast<PSOCKADDR_IN>(target)->sin_port == htons(53);
	return target->sin6_port == htons(53);
}

void DNSHandler::CreateHandler(ENDPOINT_ID id, PSOCKADDR_IN6 target, const char* packet, int length,
	PNF_UDP_OPTIONS options)
{
	if (target == nullptr || packet == nullptr || length <= 0 || options == nullptr || options->optionsLength < 0)
		return;

	ReapFinished();

	try
	{
		shared_ptr<DNSJob> job;
		multimap<ENDPOINT_ID, shared_ptr<DNSJob>>::iterator registeredJob;
		{
			lock_guard<mutex> lock(dnsJobsLock);
			if (!acceptingJobs || !activeEndpoints.contains(id))
				return;

			job = make_shared<DNSJob>(id, *target, packet, length, options, dnsAddr, dnsProx);
			registeredJob = dnsJobs.emplace(id, job);
			try
			{
				job->Start();
			}
			catch (...)
			{
				dnsJobs.erase(registeredJob);
				throw;
			}
		}
	}
	catch (...)
	{
		printf("[Redirector][DNSHandler::CreateHandler] Unable to start DNS worker\n");
	}
}
