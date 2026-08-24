#include "Based.h"
#include "EventHandler.h"
#include "IPEventHandler.h"
#include "Utils.h"

extern bool filterLoopback;
extern bool filterIntranet;
extern bool filterParent;
extern bool filterICMP;
extern bool filterTCP;
extern bool filterUDP;
extern bool filterDNS;

extern DWORD icmping;

extern bool dnsOnly;
extern bool dnsProx;
extern string dnsHost;
extern USHORT dnsPort;

extern wstring tgtHost;
extern wstring tgtPort;
extern string tgtUsername;
extern string tgtPassword;

extern vector<wstring> bypassList;
extern vector<wstring> handleList;

extern atomic_ullong UP;
extern atomic_ullong DL;

NF_EventHandler EventHandler = {
	threadStart,
	threadEnd,
	tcpConnectRequest,
	tcpConnected,
	tcpClosed,
	tcpReceive,
	tcpSend,
	tcpCanReceive,
	tcpCanSend,
	udpCreated,
	udpConnectRequest,
	udpClosed,
	udpReceive,
	udpSend,
	udpCanReceive,
	udpCanSend
};

NF_IPEventHandler IPEventHandler = {
	ipReceive,
	ipSend
};

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved)
{
	UNREFERENCED_PARAMETER(hModule);
	UNREFERENCED_PARAMETER(dwReason);
	UNREFERENCED_PARAMETER(lpReserved);

	return TRUE;
}

extern "C" {
	__declspec(dllexport) BOOL __cdecl aio_register(LPWSTR value)
	{
		auto status = nf_registerDriver(ws2s(value).c_str());
		if (status != NF_STATUS_SUCCESS)
		{
			printf("[Redirector][aio_register] nf_registerDriver: %d\n", status);
			return FALSE;
		}

		return TRUE;
	}

	__declspec(dllexport) BOOL __cdecl aio_unregister(LPWSTR value)
	{
		auto status = nf_unRegisterDriver(ws2s(value).c_str());
		if (status != NF_STATUS_SUCCESS)
		{
			printf("[Redirector][aio_unregister] nf_unRegisterDriver: %d\n", status);
			return FALSE;
		}

		return TRUE;
	}

	__declspec(dllexport) BOOL __cdecl aio_dial(int name, LPWSTR value)
	{
		switch (name)
		{
		case AIO_FILTERLOOPBACK:
			filterLoopback = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_FILTERINTRANET:
			filterIntranet = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_FILTERPARENT:
			filterParent = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_FILTERICMP:
			filterICMP = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_FILTERTCP:
			filterTCP = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_FILTERUDP:
			filterUDP = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_FILTERDNS:
			filterDNS = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_ICMPING:
			icmping = atoi(ws2s(value).c_str());
			break;
		case AIO_DNSONLY:
			dnsOnly = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_DNSPROX:
			dnsProx = (wstring(value).find(L"false") == string::npos);
			break;
		case AIO_DNSHOST:
			dnsHost = ws2s(value);
			break;
		case AIO_DNSPORT:
			dnsPort = static_cast<USHORT>(atoi(ws2s(value).c_str()));
			break;
		case AIO_TGTHOST:
			tgtHost = wstring(value);
			break;
		case AIO_TGTPORT:
			tgtPort = wstring(value);
			break;
		case AIO_TGTUSER:
			tgtUsername = ws2s(value);
			break;
		case AIO_TGTPASS:
			tgtPassword = ws2s(value);
			break;
		case AIO_CLRNAME:
			bypassList.clear();
			handleList.clear();
			break;
		case AIO_BYPNAME:
			try
			{
				std::wregex checker(value);
			}
			catch (regex_error) {
				return FALSE;
			}

			bypassList.emplace_back(value);
			break;
		case AIO_ADDNAME:
			try
			{
				std::wregex checker(value);
			}
			catch (regex_error) {
				return FALSE;
			}

			handleList.emplace_back(value);
			break;
		default:
			return FALSE;
		}

		return TRUE;
	}

	__declspec(dllexport) BOOL __cdecl aio_init()
	{
		setvbuf(stdout, nullptr, _IONBF, 0);
		setvbuf(stderr, nullptr, _IONBF, 0);

		WSADATA data;
		if (WSAStartup(MAKEWORD(2, 2), &data) != NO_ERROR)
		{
			puts("[Redirector][aio_init] WSAStartup != NO_ERROR");
			return FALSE;
		}

		nf_adjustProcessPriviledges();
		if (!eh_init())
		{
			puts("[Redirector][aio_init] !eh_init");
			WSACleanup();
			return FALSE;
		}

		if (nf_init("netfilter2", &EventHandler) != NF_STATUS_SUCCESS)
		{
			puts("[Redirector][aio_init] nf_init != NF_STATUS_SUCCESS");
			eh_begin_shutdown();
			eh_free();
			WSACleanup();
			return FALSE;
		}

		NF_RULE rule;
		auto addMappedIPv4AllowRule = [](const char* address, const char* mask)
		{
			NF_RULE mappedRule;
			memset(&mappedRule, 0, sizeof(NF_RULE));
			mappedRule.ip_family = AF_INET6;
			inet_pton(AF_INET6, address, mappedRule.remoteIpAddress);
			memset(mappedRule.remoteIpAddressMask, 0xff, 12);
			inet_pton(AF_INET, mask, mappedRule.remoteIpAddressMask + 12);
			mappedRule.filteringFlag = NF_ALLOW;
			nf_addRule(&mappedRule, FALSE);
		};

		if (!filterLoopback)
		{
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "127.0.0.1", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.0.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);

			/* IPv4 loopback represented as IPv4-mapped IPv6 (::ffff:127.0.0.0/104) */
			addMappedIPv4AllowRule("::ffff:127.0.0.0", "255.0.0.0");

			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET6;
			rule.remoteIpAddress[15] = 1;
			memset(rule.remoteIpAddressMask, 0xff, sizeof(rule.remoteIpAddressMask));
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
		}

		if (!filterIntranet)
		{
			/* 10.0.0.0/8 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "10.0.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.0.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:10.0.0.0", "255.0.0.0");

			/* 100.64.0.0/10 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "100.64.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.192.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:100.64.0.0", "255.192.0.0");

			/* 169.254.0.0/16 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "169.254.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.255.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:169.254.0.0", "255.255.0.0");

			/* 172.16.0.0/12 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "172.16.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.240.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:172.16.0.0", "255.240.0.0");

			/* 192.0.0.0/24 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "192.0.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.255.255.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:192.0.0.0", "255.255.255.0");

			/* 192.168.0.0/16 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "192.168.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.255.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:192.168.0.0", "255.255.0.0");

			/* 198.18.0.0/15 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "198.18.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.254.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:198.18.0.0", "255.254.0.0");

			/* IPv4 multicast 224.0.0.0/4 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "224.0.0.0", rule.remoteIpAddress);
			inet_pton(AF_INET, "240.0.0.0", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:224.0.0.0", "240.0.0.0");

			/* IPv4 limited broadcast 255.255.255.255/32 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			inet_pton(AF_INET, "255.255.255.255", rule.remoteIpAddress);
			inet_pton(AF_INET, "255.255.255.255", rule.remoteIpAddressMask);
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
			addMappedIPv4AllowRule("::ffff:255.255.255.255", "255.255.255.255");

			/* IPv6 unique-local addresses fc00::/7 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET6;
			inet_pton(AF_INET6, "fc00::", rule.remoteIpAddress);
			rule.remoteIpAddressMask[0] = 0xfe;
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);

			/* IPv6 link-local addresses fe80::/10 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET6;
			inet_pton(AF_INET6, "fe80::", rule.remoteIpAddress);
			rule.remoteIpAddressMask[0] = 0xff;
			rule.remoteIpAddressMask[1] = 0xc0;
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);

			/* IPv6 multicast ff00::/8 */
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET6;
			inet_pton(AF_INET6, "ff00::", rule.remoteIpAddress);
			rule.remoteIpAddressMask[0] = 0xff;
			rule.filteringFlag = NF_ALLOW;
			nf_addRule(&rule, FALSE);
		}

		if (filterICMP)
		{
			nf_setIPEventHandler(&IPEventHandler);

			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			rule.protocol = IPPROTO_ICMP;
			rule.direction = NF_D_OUT;
			rule.filteringFlag = NF_FILTER_AS_IP_PACKETS;
			nf_addRule(&rule, FALSE);
		}

		if (filterTCP)
		{
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			rule.protocol = IPPROTO_TCP;
			rule.direction = NF_D_OUT;
			rule.filteringFlag = NF_INDICATE_CONNECT_REQUESTS;
			nf_addRule(&rule, FALSE);

			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET6;
			rule.protocol = IPPROTO_TCP;
			rule.direction = NF_D_OUT;
			rule.filteringFlag = NF_INDICATE_CONNECT_REQUESTS;
			nf_addRule(&rule, FALSE);
		}

		if (filterUDP || filterDNS)
		{
			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET;
			rule.protocol = IPPROTO_UDP;
			rule.filteringFlag = NF_FILTER;
			nf_addRule(&rule, FALSE);

			memset(&rule, 0, sizeof(NF_RULE));
			rule.ip_family = AF_INET6;
			rule.protocol = IPPROTO_UDP;
			rule.filteringFlag = NF_FILTER;
			nf_addRule(&rule, FALSE);
		}

		return TRUE;
	}

	__declspec(dllexport) void __cdecl aio_free()
	{
		eh_begin_shutdown();
		nf_deleteRules();
		eh_free();
		nf_free();

		WSACleanup();
		return;
	}

	__declspec(dllexport) ULONG64 __cdecl aio_getUP()
	{
		return UP;
	}

	__declspec(dllexport) ULONG64 __cdecl aio_getDL()
	{
		return DL;
	}
}
