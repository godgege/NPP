#include "EventHandler.h"

#include "DNSHandler.h"
#include "TCPHandler.h"

extern bool filterParent;
extern bool filterTCP;
extern bool filterUDP;
extern bool filterDNS;

extern bool dnsOnly;

extern vector<wstring> bypassList;
extern vector<wstring> handleList;

extern USHORT tcpListen;

DWORD CurrentID = 0;

namespace
{
	constexpr size_t MaxProcessTreeDepth = 32;
	atomic_bool callbackShutdown = true;
	atomic_uint callbackCount = 0;
	vector<wregex> compiledHandleRules;

	void LeaveCallback()
	{
		if (callbackCount.fetch_sub(1, memory_order_acq_rel) == 1)
			callbackCount.notify_all();
	}

	class CallbackGuard
	{
	public:
		CallbackGuard()
		{
			if (callbackShutdown.load(memory_order_acquire))
				return;

			callbackCount.fetch_add(1, memory_order_acq_rel);
			if (callbackShutdown.load(memory_order_acquire))
			{
				LeaveCallback();
				return;
			}

			entered = true;
		}

		~CallbackGuard()
		{
			if (entered)
				LeaveCallback();
		}

		explicit operator bool() const
		{
			return entered;
		}

	private:
		bool entered = false;
	};

	struct UDPContext
	{
		explicit UDPContext(ENDPOINT_ID id) : id(id)
		{
		}

		~UDPContext();
		bool Send(PSOCKADDR_IN6 target, const char* buffer, int length, PNF_UDP_OPTIONS options);
		void Stop();

	private:
		void Receive();

		ENDPOINT_ID id;
		mutex operationLock;
		atomic_bool stopping = false;
		SocksHelper::UDP remote;
		thread receiveThread;
		vector<unsigned char> option;
	};
}

mutex udpContextLock;
map<ENDPOINT_ID, shared_ptr<UDPContext>> udpContext;

atomic_ullong UP = { 0 };
atomic_ullong DL = { 0 };
atomic_ullong UNMATCHED_TCP = { 0 };
atomic_ullong UNMATCHED_UDP = { 0 };

UDPContext::~UDPContext()
{
	Stop();
}

bool UDPContext::Send(PSOCKADDR_IN6 target, const char* buffer, int length, PNF_UDP_OPTIONS options)
{
	lock_guard<mutex> lock(operationLock);
	if (stopping.load(memory_order_acquire) || target == nullptr || buffer == nullptr ||
		options == nullptr || options->optionsLength < 0)
		return false;

	if (!remote.IsAssociated() && !remote.Associate())
		return false;
	if (!remote.IsReady() && !remote.CreateUDP())
		return false;

	if (!receiveThread.joinable())
	{
		try
		{
			auto optionSize = sizeof(NF_UDP_OPTIONS) +
				(options->optionsLength > 0 ? static_cast<size_t>(options->optionsLength) - 1 : 0);
			option.resize(optionSize);
			memcpy(option.data(), options, optionSize);
			receiveThread = thread([this] { Receive(); });
		}
		catch (...)
		{
			remote.Stop();
			return false;
		}
	}

	return remote.Send(target, buffer, length) == length;
}

void UDPContext::Stop()
{
	stopping.store(true, memory_order_release);
	thread worker;
	{
		lock_guard<mutex> lock(operationLock);
		remote.Stop();
		if (receiveThread.joinable())
			worker = move(receiveThread);
	}

	if (worker.joinable())
		worker.join();
}

void UDPContext::Receive()
{
	vector<char> buffer(65536);
	while (!stopping.load(memory_order_acquire))
	{
		SOCKADDR_IN6 target{};
		int length = remote.Read(&target, buffer.data(), static_cast<int>(buffer.size()), nullptr);
		if (length == 0)
			continue;
		if (length == SOCKET_ERROR)
			break;
		if (stopping.load(memory_order_acquire))
			break;

		DL += length;
		nf_udpPostReceive(id, reinterpret_cast<unsigned char*>(&target), buffer.data(), length,
			reinterpret_cast<PNF_UDP_OPTIONS>(option.data()));
	}

	remote.Stop();
}

wstring ConvertIP(PSOCKADDR addr)
{
	WCHAR buffer[MAX_PATH] = L"";
	DWORD bufferLength = MAX_PATH;

	if (addr->sa_family == AF_INET)
	{
		WSAAddressToStringW(addr, sizeof(SOCKADDR_IN), NULL, buffer, &bufferLength);
	}
	else
	{
		WSAAddressToStringW(addr, sizeof(SOCKADDR_IN6), NULL, buffer, &bufferLength);
	}

	return buffer;
}

wstring GetProcessName(DWORD id)
{
	if (id == 0)
	{
		return L"Idle";
	}

	if (id == 4)
	{
		return L"System";
	}

	wchar_t name[MAX_PATH];
	if (!nf_getProcessNameFromKernel(id, name, MAX_PATH))
	{
		if (!nf_getProcessNameW(id, name, MAX_PATH))
		{
			return L"Unknown";
		}
	}

	wchar_t data[MAX_PATH];
	if (GetLongPathNameW(name, data, MAX_PATH))
	{
		return data;
	}

	return name;
}

bool TryGetProcessIdentity(DWORD id, wstring& name, FILETIME& creationTime)
{
	auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, id);
	if (process == NULL)
		return false;

	FILETIME exitTime{};
	FILETIME kernelTime{};
	FILETIME userTime{};
	if (!GetProcessTimes(process, &creationTime, &exitTime, &kernelTime, &userTime))
	{
		CloseHandle(process);
		return false;
	}

	DWORD pathLength = 32768;
	vector<wchar_t> path(pathLength);
	if (!QueryFullProcessImageNameW(process, 0, path.data(), &pathLength))
	{
		CloseHandle(process);
		return false;
	}

	CloseHandle(process);
	name.assign(path.data(), pathLength);

	vector<wchar_t> longPath(32768);
	auto longPathLength = GetLongPathNameW(name.c_str(), longPath.data(), static_cast<DWORD>(longPath.size()));
	if (longPathLength > 0 && longPathLength < static_cast<DWORD>(longPath.size()))
		name.assign(longPath.data(), longPathLength);

	return true;
}

bool checkBypassName(DWORD id)
{
	auto name = GetProcessName(id);

	for (size_t i = 0; i < bypassList.size(); i++)
	{
		if (regex_search(name, wregex(bypassList[i])))
		{
			return true;
		}
	}

	return false;
}

bool matchesHandleName(const wstring& name)
{
	for (const auto& rule : compiledHandleRules)
	{
		if (regex_search(name, rule))
		{
			return true;
		}
	}

	return false;
}

bool matchesHandleName(DWORD id)
{
	return matchesHandleName(GetProcessName(id));
}

bool checkHandleName(DWORD id)
{
	if (matchesHandleName(id))
		return true;

	if (!filterParent)
		return false;

	PROCESSENTRY32W PE;
	memset(&PE, 0, sizeof(PROCESSENTRY32W));
	PE.dwSize = sizeof(PROCESSENTRY32W);

	auto hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
	if (hSnapshot == INVALID_HANDLE_VALUE)
		return false;

	if (!Process32FirstW(hSnapshot, &PE))
	{
		CloseHandle(hSnapshot);
		return false;
	}

	map<DWORD, DWORD> parentByProcessId;
	do
	{
		parentByProcessId[PE.th32ProcessID] = PE.th32ParentProcessID;
	} while (Process32NextW(hSnapshot, &PE));
	CloseHandle(hSnapshot);

	set<DWORD> visitedProcessIds{ id };
	auto currentId = id;
	wstring currentName;
	FILETIME currentCreationTime{};
	if (!TryGetProcessIdentity(currentId, currentName, currentCreationTime))
		return false;

	for (size_t depth = 0; depth < MaxProcessTreeDepth; depth++)
	{
		auto parentEntry = parentByProcessId.find(currentId);
		if (parentEntry == parentByProcessId.end())
			break;

		auto parentId = parentEntry->second;
		if (parentId == 0 || !visitedProcessIds.insert(parentId).second)
			break;

		wstring parentName;
		FILETIME parentCreationTime{};
		if (!TryGetProcessIdentity(parentId, parentName, parentCreationTime) ||
			CompareFileTime(&parentCreationTime, &currentCreationTime) > 0)
			break;

		if (matchesHandleName(parentName))
			return true;

		currentId = parentId;
		currentCreationTime = parentCreationTime;
	}

	return false;
}

bool eh_init()
{
	callbackShutdown.store(true, memory_order_release);
	CurrentID = GetCurrentProcessId();
	UNMATCHED_TCP = 0;
	UNMATCHED_UDP = 0;
	compiledHandleRules.clear();
	try
	{
		compiledHandleRules.reserve(handleList.size());
		for (const auto& rule : handleList)
			compiledHandleRules.emplace_back(rule, regex_constants::ECMAScript | regex_constants::icase);
	}
	catch (const regex_error&)
	{
		compiledHandleRules.clear();
		return false;
	}

	if (!DNSHandler::INIT())
	{
		compiledHandleRules.clear();
		return false;
	}

	if (!TCPHandler::INIT())
	{
		DNSHandler::FREE();
		compiledHandleRules.clear();
		return false;
	}

	callbackShutdown.store(false, memory_order_release);
	return true;
}

void eh_begin_shutdown()
{
	callbackShutdown.store(true, memory_order_release);
}

void eh_free()
{
	auto active = callbackCount.load(memory_order_acquire);
	while (active != 0)
	{
		callbackCount.wait(active, memory_order_acquire);
		active = callbackCount.load(memory_order_acquire);
	}

	vector<shared_ptr<UDPContext>> contexts;
	{
		lock_guard<mutex> lock(udpContextLock);
		contexts.reserve(udpContext.size());
		for (auto& context : udpContext)
			contexts.emplace_back(move(context.second));
		udpContext.clear();
	}

	for (const auto& context : contexts)
		context->Stop();

	DNSHandler::FREE();
	TCPHandler::FREE();
	compiledHandleRules.clear();

	UP = 0;
	DL = 0;
}

void threadStart()
{

}

void threadEnd()
{

}

void tcpConnectRequest(ENDPOINT_ID id, PNF_TCP_CONN_INFO info)
{
	CallbackGuard guard;
	if (!guard)
		return;

	if (CurrentID == info->processId)
	{
		nf_tcpDisableFiltering(id);
		return;
	}

	if (!filterTCP)
	{
		nf_tcpDisableFiltering(id);

		wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][!filterTCP] " << GetProcessName(info->processId) << endl;
		return;
	}

	if (checkBypassName(info->processId))
	{
		nf_tcpDisableFiltering(id);

		wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][checkBypassName] " << GetProcessName(info->processId) << endl;
		return;
	}

	if (!checkHandleName(info->processId))
	{
		nf_tcpDisableFiltering(id);

		// Log only logarithmic samples (1, 2, 4, 8, ...) so unrelated
		// applications remain diagnosable without writing one line per flow.
		auto unmatchedCount = ++UNMATCHED_TCP;
		if ((unmatchedCount & (unmatchedCount - 1)) == 0)
			wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][!checkHandleName][sample " << unmatchedCount << "] " << GetProcessName(info->processId) << endl;
		return;
	}

	if (info->ip_family != AF_INET && info->ip_family != AF_INET6)
	{
		nf_tcpDisableFiltering(id);

		wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "][!IPv4 && !IPv6] " << GetProcessName(info->processId) << endl;
		return;
	}

	SOCKADDR_IN6 client;
	memcpy(&client, info->localAddress, sizeof(SOCKADDR_IN6));

	SOCKADDR_IN6 remote;
	memcpy(&remote, info->remoteAddress, sizeof(SOCKADDR_IN6));

	if (info->ip_family == AF_INET)
	{
		auto addr = (PSOCKADDR_IN)info->remoteAddress;
		addr->sin_family = AF_INET;
		addr->sin_addr.S_un.S_addr = htonl(INADDR_LOOPBACK);
		addr->sin_port = tcpListen;
	}

	if (info->ip_family == AF_INET6)
	{
		auto addr = (PSOCKADDR_IN6)info->remoteAddress;
		IN6ADDR_SETLOOPBACK(addr);
		addr->sin6_port = tcpListen;
	}

	TCPHandler::CreateHandler(client, remote);
	wcout << "[Redirector][EventHandler][tcpConnectRequest][" << id << "][" << info->processId << "] " << ConvertIP((PSOCKADDR)&client) << " -> " << ConvertIP((PSOCKADDR)&remote) << endl;
}

void tcpConnected(ENDPOINT_ID id, PNF_TCP_CONN_INFO info)
{
	CallbackGuard guard;
	if (!guard)
		return;

	wcout << "[Redirector][EventHandler][tcpConnected][" << id << "][" << info->processId << "][" << ConvertIP((PSOCKADDR)info->remoteAddress) << "] " << GetProcessName(info->processId) << endl;
}

void tcpCanSend(ENDPOINT_ID id)
{
	CallbackGuard guard;
	if (!guard)
		return;

	UNREFERENCED_PARAMETER(id);
}

void tcpSend(ENDPOINT_ID id, const char* buffer, int length)
{
	CallbackGuard guard;
	if (!guard)
		return;

	UP += length;

	nf_tcpPostSend(id, buffer, length);
}

void tcpCanReceive(ENDPOINT_ID id)
{
	CallbackGuard guard;
	if (!guard)
		return;

	UNREFERENCED_PARAMETER(id);
}

void tcpReceive(ENDPOINT_ID id, const char* buffer, int length)
{
	CallbackGuard guard;
	if (!guard)
		return;

	DL += length;

	nf_tcpPostReceive(id, buffer, length);
}

void tcpClosed(ENDPOINT_ID id, PNF_TCP_CONN_INFO info)
{
	CallbackGuard guard;
	if (!guard)
		return;

	SOCKADDR_IN6 client;
	memcpy(&client, info->localAddress, sizeof(SOCKADDR_IN6));

	if (TCPHandler::DeleteHandler(client))
		printf("[Redirector][EventHandler][tcpClosed][%llu][%lu]\n", id, info->processId);
}

void udpCreated(ENDPOINT_ID id, PNF_UDP_CONN_INFO info)
{
	CallbackGuard guard;
	if (!guard)
		return;

	if (CurrentID == info->processId)
	{
		nf_udpDisableFiltering(id);
		return;
	}

	if (!filterUDP)
	{
		if (!filterDNS) nf_udpDisableFiltering(id);

		wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "][!filterUDP] " << GetProcessName(info->processId) << endl;
		return;
	}

	if (checkBypassName(info->processId))
	{
		if (dnsOnly) nf_udpDisableFiltering(id);

		wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "][checkBypassName] " << GetProcessName(info->processId) << endl;
		return;
	}

	if (!checkHandleName(info->processId))
	{
		if (dnsOnly) nf_udpDisableFiltering(id);

		auto unmatchedCount = ++UNMATCHED_UDP;
		if ((unmatchedCount & (unmatchedCount - 1)) == 0)
			wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "][!checkHandleName][sample " << unmatchedCount << "] " << GetProcessName(info->processId) << endl;
		return;
	}

	wcout << "[Redirector][EventHandler][udpCreated][" << id << "][" << info->processId << "] " << GetProcessName(info->processId) << endl;

	DNSHandler::Activate(id);
	auto context = make_shared<UDPContext>(id);
	shared_ptr<UDPContext> oldContext;
	{
		lock_guard<mutex> lock(udpContextLock);
		auto existing = udpContext.find(id);
		if (existing != udpContext.end())
		{
			oldContext = move(existing->second);
			existing->second = move(context);
		}
		else
		{
			udpContext.emplace(id, move(context));
		}
	}

	if (oldContext)
		oldContext->Stop();
}

void udpConnectRequest(ENDPOINT_ID id, PNF_UDP_CONN_REQUEST info)
{
	CallbackGuard guard;
	if (!guard)
		return;

	UNREFERENCED_PARAMETER(id);
	UNREFERENCED_PARAMETER(info);
}

void udpCanSend(ENDPOINT_ID id)
{
	CallbackGuard guard;
	if (!guard)
		return;

	UNREFERENCED_PARAMETER(id);
}

void udpSend(ENDPOINT_ID id, const unsigned char* target, const char* buffer, int length, PNF_UDP_OPTIONS options)
{
	CallbackGuard guard;
	if (!guard)
		return;

	if (DNSHandler::IsDNS((PSOCKADDR_IN6)target))
	{
		if (!filterDNS)
		{
			nf_udpPostSend(id, target, buffer, length, options);

			wcout << "[Redirector][EventHandler][udpSend][" << id << "] B DNS to " << ConvertIP((PSOCKADDR)target) << endl;
			return;
		}
		else
		{
			UP += length;
			DNSHandler::CreateHandler(id, (PSOCKADDR_IN6)target, buffer, length, options);

			wcout << "[Redirector][EventHandler][udpSend][" << id << "] H DNS to " << ConvertIP((PSOCKADDR)target) << endl;
			return;
		}
	}

	shared_ptr<UDPContext> context;
	{
		lock_guard<mutex> lock(udpContextLock);
		auto found = udpContext.find(id);
		if (found != udpContext.end())
			context = found->second;
	}

	if (!context)
	{
		nf_udpPostSend(id, target, buffer, length, options);
		return;
	}

	if (context->Send((PSOCKADDR_IN6)target, buffer, length, options))
		UP += length;
}

void udpCanReceive(ENDPOINT_ID id)
{
	CallbackGuard guard;
	if (!guard)
		return;

	UNREFERENCED_PARAMETER(id);
}

void udpReceive(ENDPOINT_ID id, const unsigned char* target, const char* buffer, int length, PNF_UDP_OPTIONS options)
{
	CallbackGuard guard;
	if (!guard)
		return;

	nf_udpPostReceive(id, target, buffer, length, options);
}

void udpClosed(ENDPOINT_ID id, PNF_UDP_CONN_INFO info)
{
	CallbackGuard guard;
	if (!guard)
		return;

	UNREFERENCED_PARAMETER(info);

	DNSHandler::Cancel(id);
	shared_ptr<UDPContext> context;
	{
		lock_guard<mutex> lock(udpContextLock);
		auto found = udpContext.find(id);
		if (found != udpContext.end())
		{
			context = move(found->second);
			udpContext.erase(found);
		}
	}

	if (!context)
		return;

	printf("[Redirector][EventHandler][udpClosed][%llu]\n", id);
	context->Stop();
}
