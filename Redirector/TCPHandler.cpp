#include "TCPHandler.h"

USHORT tcpListen = 0;

namespace
{
	constexpr long ACCEPT_POLL_MICROSECONDS = 200000;

	class TCPConnection
	{
	public:
		TCPConnection(SOCKET client, const SOCKADDR_IN6& target) : clientSocket(client), target(target)
		{
		}

		~TCPConnection()
		{
			RequestStop();
			Join();
			CloseClient();
		}

		TCPConnection(const TCPConnection&) = delete;
		TCPConnection& operator=(const TCPConnection&) = delete;

		void Start()
		{
			worker = thread([this] { Run(); });
		}

		void RequestStop()
		{
			stopping.store(true, memory_order_release);
			{
				lock_guard<mutex> lock(clientLock);
				if (clientSocket != INVALID_SOCKET)
					shutdown(clientSocket, SD_BOTH);
			}
			remote.RequestStop();
		}

		void Join()
		{
			if (worker.joinable())
				worker.join();
		}

		bool Finished() const noexcept
		{
			return finished.load(memory_order_acquire);
		}

	private:
		void Run() noexcept
		{
			thread uploadWorker;
			try
			{
				if (!stopping.load(memory_order_acquire) && remote.Connect(&target) &&
					!stopping.load(memory_order_acquire))
				{
					uploadWorker = thread([this] {
						PumpToRemote();
						RequestStop();
					});
					PumpFromRemote();
				}
			}
			catch (...)
			{
				printf("[Redirector][TCPHandler::TCPConnection] Worker failed\n");
			}

			RequestStop();
			if (uploadWorker.joinable())
				uploadWorker.join();
			remote.Stop();
			CloseClient();
			finished.store(true, memory_order_release);
		}

		void PumpFromRemote()
		{
			char buffer[16384];
			while (!stopping.load(memory_order_acquire))
			{
				const int length = remote.Read(buffer, sizeof(buffer));
				if (length <= 0 || !SendAllClient(buffer, length))
					break;
			}
		}

		void PumpToRemote()
		{
			char buffer[16384];
			while (!stopping.load(memory_order_acquire))
			{
				SOCKET client = INVALID_SOCKET;
				{
					lock_guard<mutex> lock(clientLock);
					client = clientSocket;
				}
				if (client == INVALID_SOCKET)
					break;

				const int length = recv(client, buffer, sizeof(buffer), 0);
				if (length <= 0 || !SendAllRemote(buffer, length))
					break;
			}
		}

		bool SendAllClient(const char* buffer, int length)
		{
			int sent = 0;
			while (sent < length && !stopping.load(memory_order_acquire))
			{
				SOCKET client = INVALID_SOCKET;
				{
					lock_guard<mutex> lock(clientLock);
					client = clientSocket;
				}
				if (client == INVALID_SOCKET)
					return false;

				const int current = send(client, buffer + sent, length - sent, 0);
				if (current <= 0)
					return false;
				sent += current;
			}
			return sent == length;
		}

		bool SendAllRemote(const char* buffer, int length)
		{
			int sent = 0;
			while (sent < length && !stopping.load(memory_order_acquire))
			{
				const int current = remote.Send(buffer + sent, length - sent);
				if (current <= 0)
					return false;
				sent += current;
			}
			return sent == length;
		}

		void CloseClient()
		{
			SOCKET client = INVALID_SOCKET;
			{
				lock_guard<mutex> lock(clientLock);
				client = exchange(clientSocket, INVALID_SOCKET);
			}
			if (client != INVALID_SOCKET)
				closesocket(client);
		}

		mutex clientLock;
		SOCKET clientSocket = INVALID_SOCKET;
		SOCKADDR_IN6 target{};
		SocksHelper::TCP remote;
		atomic_bool stopping = false;
		atomic_bool finished = false;
		thread worker;
	};

	mutex lifecycleLock;
	mutex stateLock;
	SOCKET listenSocket = INVALID_SOCKET;
	thread acceptWorker;

	struct RouteKey
	{
		int family;
		USHORT port;

		bool operator<(const RouteKey& other) const noexcept
		{
			return family < other.family || (family == other.family && port < other.port);
		}
	};

	map<RouteKey, SOCKADDR_IN6> routes;
	set<RouteKey> claimedRoutes;
	map<unsigned long long, shared_ptr<TCPConnection>> connections;
	unsigned long long nextConnectionId = 1;

	RouteKey GetRouteKey(const SOCKADDR_IN6& address)
	{
		if (address.sin6_family == AF_INET)
			return { AF_INET, reinterpret_cast<const SOCKADDR_IN*>(&address)->sin_port };
		if (address.sin6_family == AF_INET6 && IN6_IS_ADDR_V4MAPPED(&address.sin6_addr))
			return { AF_INET, address.sin6_port };
		return { AF_INET6, address.sin6_port };
	}

	bool IsActiveListener(SOCKET listener)
	{
		lock_guard<mutex> lock(stateLock);
		return listenSocket == listener;
	}

	void JoinContexts(vector<shared_ptr<TCPConnection>>& contexts)
	{
		for (const auto& context : contexts)
			context->RequestStop();
		for (const auto& context : contexts)
			context->Join();
	}

	void ReapFinished()
	{
		vector<shared_ptr<TCPConnection>> finished;
		{
			lock_guard<mutex> lock(stateLock);
			for (auto connection = connections.begin(); connection != connections.end();)
			{
				if (connection->second->Finished())
				{
					finished.emplace_back(move(connection->second));
					connection = connections.erase(connection);
				}
				else
				{
					++connection;
				}
			}
		}

		for (const auto& context : finished)
			context->Join();
	}

	void RegisterConnection(SOCKET listener, SOCKET accepted)
	{
		SOCKADDR_IN6 peer{};
		int peerLength = sizeof(peer);
		if (getpeername(accepted, reinterpret_cast<PSOCKADDR>(&peer), &peerLength) == SOCKET_ERROR)
		{
			closesocket(accepted);
			return;
		}

		u_long blocking = 0;
		if (ioctlsocket(accepted, FIONBIO, &blocking) == SOCKET_ERROR)
		{
			printf("[Redirector][TCPHandler::Accept] Set accepted socket blocking failed: %d\n", WSAGetLastError());
			closesocket(accepted);
			return;
		}

		shared_ptr<TCPConnection> context;
		try
		{
			SOCKADDR_IN6 target{};
			{
				lock_guard<mutex> lock(stateLock);
				const auto route = routes.find(GetRouteKey(peer));
				if (listenSocket != listener || route == routes.end())
				{
					closesocket(accepted);
					return;
				}
				target = route->second;
				claimedRoutes.insert(route->first);
				routes.erase(route);
			}

			context = make_shared<TCPConnection>(accepted, target);
			accepted = INVALID_SOCKET;

			lock_guard<mutex> lock(stateLock);
			if (listenSocket != listener)
				return;

			const auto id = nextConnectionId++;
			connections.emplace(id, context);
			try
			{
				context->Start();
			}
			catch (...)
			{
				connections.erase(id);
				throw;
			}
		}
		catch (...)
		{
			printf("[Redirector][TCPHandler::Accept] Unable to start connection worker\n");
			if (accepted != INVALID_SOCKET)
				closesocket(accepted);
		}
	}

	void AcceptLoop(SOCKET listener)
	{
		while (IsActiveListener(listener))
		{
			ReapFinished();

			fd_set readable;
			FD_ZERO(&readable);
			FD_SET(listener, &readable);
			timeval timeout{};
			timeout.tv_usec = ACCEPT_POLL_MICROSECONDS;

			const int ready = select(0, &readable, nullptr, nullptr, &timeout);
			if (ready == 0)
				continue;
			if (ready == SOCKET_ERROR)
			{
				if (IsActiveListener(listener))
					printf("[Redirector][TCPHandler::Accept] Select failed: %d\n", WSAGetLastError());
				return;
			}
			if (!IsActiveListener(listener))
				return;

			const SOCKET accepted = accept(listener, nullptr, nullptr);
			if (accepted == INVALID_SOCKET)
			{
				const int error = WSAGetLastError();
				if (error == WSAEWOULDBLOCK)
					continue;
				if (IsActiveListener(listener))
					printf("[Redirector][TCPHandler::Accept] Accept client failed: %d\n", error);
				return;
			}

			RegisterConnection(listener, accepted);
		}
	}

	void FreeLocked()
	{
		SOCKET listener = INVALID_SOCKET;
		thread listenerWorker;
		{
			lock_guard<mutex> lock(stateLock);
			listener = exchange(listenSocket, INVALID_SOCKET);
			tcpListen = 0;
			routes.clear();
			claimedRoutes.clear();
			if (acceptWorker.joinable())
				listenerWorker = move(acceptWorker);
		}

		if (listenerWorker.joinable())
			listenerWorker.join();
		if (listener != INVALID_SOCKET)
			closesocket(listener);

		vector<shared_ptr<TCPConnection>> active;
		{
			lock_guard<mutex> lock(stateLock);
			active.reserve(connections.size());
			for (auto& connection : connections)
				active.emplace_back(move(connection.second));
			connections.clear();
		}

		JoinContexts(active);
	}
}

bool TCPHandler::INIT()
{
	lock_guard<mutex> lifecycle(lifecycleLock);
	FreeLocked();

	SOCKET listener = socket(AF_INET6, SOCK_STREAM, IPPROTO_TCP);
	if (listener == INVALID_SOCKET)
	{
		printf("[Redirector][TCPHandler::INIT] Create socket failed: %d\n", WSAGetLastError());
		return false;
	}

	int v6only = 0;
	if (setsockopt(listener, IPPROTO_IPV6, IPV6_V6ONLY, reinterpret_cast<char*>(&v6only), sizeof(v6only)) == SOCKET_ERROR)
	{
		printf("[Redirector][TCPHandler::INIT] Set socket option failed: %d\n", WSAGetLastError());
		closesocket(listener);
		return false;
	}

	SOCKADDR_IN6 address{};
	IN6ADDR_SETANY(&address);
	if (bind(listener, reinterpret_cast<PSOCKADDR>(&address), sizeof(address)) == SOCKET_ERROR)
	{
		printf("[Redirector][TCPHandler::INIT] Bind socket failed: %d\n", WSAGetLastError());
		closesocket(listener);
		return false;
	}

	if (listen(listener, 1024) == SOCKET_ERROR)
	{
		printf("[Redirector][TCPHandler::INIT] Listen socket failed: %d\n", WSAGetLastError());
		closesocket(listener);
		return false;
	}

	u_long nonBlocking = 1;
	if (ioctlsocket(listener, FIONBIO, &nonBlocking) == SOCKET_ERROR)
	{
		printf("[Redirector][TCPHandler::INIT] Set nonblocking failed: %d\n", WSAGetLastError());
		closesocket(listener);
		return false;
	}

	int addressLength = sizeof(address);
	if (getsockname(listener, reinterpret_cast<PSOCKADDR>(&address), &addressLength) == SOCKET_ERROR)
	{
		printf("[Redirector][TCPHandler::INIT] Get listen address failed: %d\n", WSAGetLastError());
		closesocket(listener);
		return false;
	}

	try
	{
		lock_guard<mutex> lock(stateLock);
		listenSocket = listener;
		tcpListen = address.sin6_port;
		acceptWorker = thread([listener] { AcceptLoop(listener); });
	}
	catch (...)
	{
		lock_guard<mutex> lock(stateLock);
		listenSocket = INVALID_SOCKET;
		tcpListen = 0;
		closesocket(listener);
		printf("[Redirector][TCPHandler::INIT] Unable to start accept worker\n");
		return false;
	}

	return true;
}

void TCPHandler::FREE()
{
	lock_guard<mutex> lifecycle(lifecycleLock);
	FreeLocked();
}

void TCPHandler::CreateHandler(SOCKADDR_IN6 client, SOCKADDR_IN6 remote)
{
	lock_guard<mutex> lock(stateLock);
	if (listenSocket != INVALID_SOCKET)
		routes[GetRouteKey(client)] = remote;
}

bool TCPHandler::DeleteHandler(SOCKADDR_IN6 client)
{
	bool removed = false;
	{
		lock_guard<mutex> lock(stateLock);
		const auto key = GetRouteKey(client);
		removed = routes.erase(key) != 0;
		removed = claimedRoutes.erase(key) != 0 || removed;
	}

	ReapFinished();
	return removed;
}
