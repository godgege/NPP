#include "SocksHelper.h"

#include "Utils.h"

extern wstring tgtHost;
extern wstring tgtPort;
extern string tgtUsername;
extern string tgtPassword;

namespace
{
	bool SendAll(SOCKET socket, const char* buffer, int length)
	{
		int sent = 0;
		while (sent < length)
		{
			const int current = send(socket, buffer + sent, length - sent, 0);
			if (current <= 0)
				return false;
			sent += current;
		}
		return true;
	}

	bool ReceiveExact(SOCKET socket, char* buffer, int length)
	{
		int received = 0;
		while (received < length)
		{
			const int current = recv(socket, buffer + received, length - received, 0);
			if (current <= 0)
				return false;
			received += current;
		}
		return true;
	}
}

SOCKET SocksHelper::Connect()
{
	auto client = socket(AF_INET6, SOCK_STREAM, IPPROTO_TCP);
	if (client == INVALID_SOCKET)
	{
		printf("[Redirector][SocksHelper::Connect] Create socket failed: %d\n", WSAGetLastError());
		return INVALID_SOCKET;
	}

	{
		int v6only = 0;
		if (setsockopt(client, IPPROTO_IPV6, IPV6_V6ONLY, (char*)&v6only, sizeof(v6only)) == SOCKET_ERROR)
		{
			printf("[Redirector][SocksHelper::Connect] Set socket option failed: %d\n", WSAGetLastError());

			closesocket(client);
			return INVALID_SOCKET;
		}
	}

	timeval timeout{};
	timeout.tv_sec = 4;

	if (!WSAConnectByNameW(client, (LPWSTR)tgtHost.c_str(), (LPWSTR)tgtPort.c_str(), NULL, NULL, NULL, NULL, &timeout, NULL))
	{
		printf("[Redirector][SocksHelper::Connect] Connect to remote server failed: %d\n", WSAGetLastError());

		closesocket(client);
		return INVALID_SOCKET;
	}

	{
		DWORD returned = 0;

		tcp_keepalive data = { 1, 120000, 10000 };
		WSAIoctl(client, SIO_KEEPALIVE_VALS, &data, sizeof(data), NULL, 0, &returned, NULL, NULL);
	}

	return client;
}

bool SocksHelper::Handshake(SOCKET client)
{
	char buffer[1024];
	memset(buffer, 0, sizeof(buffer));

	/* Client Hello */
	buffer[0] = 0x05;
	buffer[1] = 0x02;
	buffer[2] = 0x00;
	buffer[3] = 0x02;
	if (!SendAll(client, buffer, 4))
	{
		printf("[Redirector][SocksHelper::Handshake] Send client hello failed: %d\n", WSAGetLastError());
		return false;
	}

	/* Server Choice */
	if (!ReceiveExact(client, buffer, 2))
	{
		printf("[Redirector][SocksHelper::Handshake] Receive server choice failed: %d\n", WSAGetLastError());
		return false;
	}

	/* Authentication */
	if (buffer[1] == 0x02)
	{
		memset(buffer, 0, sizeof(buffer));
		buffer[0] = 0x01;

		BYTE ulength = tgtUsername.length() & 0xff;
		BYTE plength = tgtPassword.length() & 0xff;

		/* Username */
		buffer[1] = 0x00;
		if (ulength != 0)
		{
			buffer[1] = ulength;
			memcpy(buffer + 1 + 1, tgtUsername.c_str(), ulength);
		}

		/* Password */
		buffer[1 + 1 + ulength] = 0x00;
		if (plength != 0)
		{
			buffer[1 + 1 + ulength] = plength;
			memcpy(buffer + 1 + 1 + ulength + 1, tgtPassword.c_str(), plength);
		}

		auto length = 1 + 1 + ulength + 1 + plength;
		if (!SendAll(client, buffer, length))
		{
			printf("[Redirector][SocksHelper::Handshake] Send authentication request failed: %d\n", WSAGetLastError());
			return false;
		}

		/* Server Response */
		if (!ReceiveExact(client, buffer, 2))
		{
			printf("[Redirector][SocksHelper::Handshake] Receive server response failed: %d\n", WSAGetLastError());
			return false;
		}

		if (buffer[1] != 0x00)
		{
			puts("[Redirector][SocksHelper::Handshake] Authentication failed");
			return false;
		}
	}
	else if (buffer[1] != 0x00)
	{
		return false;
	}

	return true;
}

bool SocksHelper::SplitAddr(SOCKET client, PSOCKADDR_IN6 addr)
{
	char addrType;
	if (!ReceiveExact(client, reinterpret_cast<char*>(&addrType), 1))
	{
		printf("[Redirector][SocksHelper::SplitAddr] Read address type failed: %d\n", WSAGetLastError());
		return false;
	}

	if (addrType == 0x01)
	{
		auto ipv4 = (PSOCKADDR_IN)addr;
		ipv4->sin_family = AF_INET;

		if (!ReceiveExact(client, reinterpret_cast<char*>(&ipv4->sin_addr), 4))
		{
			printf("[Redirector][SocksHelper::SplitAddr] Read IPv4 address failed: %d\n", WSAGetLastError());
			return false;
		}

		if (!ReceiveExact(client, reinterpret_cast<char*>(&ipv4->sin_port), 2))
		{
			printf("[Redirector][SocksHelper::SplitAddr] Read IPv4 port failed: %d\n", WSAGetLastError());
			return false;
		}
	}
	else if (addrType == 0x04)
	{
		addr->sin6_family = AF_INET6;

		if (!ReceiveExact(client, reinterpret_cast<char*>(&addr->sin6_addr), 16))
		{
			printf("[Redirector][SocksHelper::SplitAddr] Read IPv6 address failed: %d\n", WSAGetLastError());
			return false;
		}

		if (!ReceiveExact(client, reinterpret_cast<char*>(&addr->sin6_port), 2))
		{
			printf("[Redirector][SocksHelper::SplitAddr] Read IPv6 port failed: %d\n", WSAGetLastError());
			return false;
		}
	}
	else
	{
		printf("[Redirector][SocksHelper::SplitAddr] Unsupported address family: %d\n", addrType);
		return false;
	}

	return true;
}

SocksHelper::TCP::~TCP()
{
	Stop();
}

bool SocksHelper::TCP::Connect(PSOCKADDR_IN6 target)
{
	if (target == nullptr || (target->sin6_family != AF_INET && target->sin6_family != AF_INET6))
		return false;

	{
		lock_guard<mutex> lock(this->stateLock);
		if (this->stopping)
			return false;
		if (this->tcpSocket != INVALID_SOCKET)
			return true;

		++this->activeOperations;
	}
	OperationLease operation(*this);

	SOCKET client = SocksHelper::Connect();
	bool connected = false;
	bool permitted = true;
	if (client != INVALID_SOCKET)
	{
		lock_guard<mutex> lock(this->stateLock);
		if (!this->stopping)
			this->pendingSocket = client;
		else
			permitted = false;
	}
	do
	{
		if (client == INVALID_SOCKET || !permitted)
			break;

		DWORD timeout = 4000;
		if (setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout)) == SOCKET_ERROR ||
			setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout)) == SOCKET_ERROR)
		{
			printf("[Redirector][SocksHelper::TCP::Connect] Set socket timeout failed: %d\n", WSAGetLastError());
			break;
		}

		if (!SocksHelper::Handshake(client))
			break;

		/* Connect Request */
		if (target->sin6_family == AF_INET)
		{
			char request[10]{};
			request[0] = 0x05;
			request[1] = 0x01;
			request[3] = 0x01;

			auto address = reinterpret_cast<PSOCKADDR_IN>(target);
			memcpy(request + 4, &address->sin_addr, 4);
			memcpy(request + 8, &address->sin_port, 2);

			if (!SendAll(client, request, sizeof(request)))
			{
				printf("[Redirector][SocksHelper::TCP::Connect] Send connect request failed: %d\n", WSAGetLastError());
				break;
			}
		}
		else
		{
			char request[22]{};
			request[0] = 0x05;
			request[1] = 0x01;
			request[3] = 0x04;

			memcpy(request + 4, &target->sin6_addr, 16);
			memcpy(request + 20, &target->sin6_port, 2);

			if (!SendAll(client, request, sizeof(request)))
			{
				printf("[Redirector][SocksHelper::TCP::Connect] Send connect request failed: %d\n", WSAGetLastError());
				break;
			}
		}

		/* Server Response */
		char response[3];
		if (!ReceiveExact(client, response, sizeof(response)))
		{
			printf("[Redirector][SocksHelper::TCP::Connect] Receive server response failed: %d\n", WSAGetLastError());
			break;
		}

		if (response[1] != 0x00)
			break;

		SOCKADDR_IN6 address{};
		if (!SocksHelper::SplitAddr(client, &address))
			break;

		connected = true;
	} while (false);

	{
		lock_guard<mutex> lock(this->stateLock);
		if (this->pendingSocket == client)
			this->pendingSocket = INVALID_SOCKET;

		if (connected && !this->stopping)
		{
			if (this->tcpSocket == INVALID_SOCKET)
			{
				this->tcpSocket = client;
				client = INVALID_SOCKET;
			}
			connected = this->tcpSocket != INVALID_SOCKET;
		}
		else
		{
			connected = false;
		}
	}

	if (client != INVALID_SOCKET)
		closesocket(client);

	return connected;
}

void SocksHelper::TCP::RequestStop()
{
	lock_guard<mutex> lock(this->stateLock);
	this->stopping = true;
	if (this->tcpSocket != INVALID_SOCKET)
		shutdown(this->tcpSocket, SD_BOTH);
	if (this->pendingSocket != INVALID_SOCKET)
		shutdown(this->pendingSocket, SD_BOTH);
}

void SocksHelper::TCP::Stop()
{
	RequestStop();

	SOCKET client = INVALID_SOCKET;
	SOCKET pending = INVALID_SOCKET;
	{
		unique_lock<mutex> lock(this->stateLock);
		this->stateChanged.wait(lock, [this] { return this->activeOperations == 0; });
		client = exchange(this->tcpSocket, INVALID_SOCKET);
		pending = exchange(this->pendingSocket, INVALID_SOCKET);
	}

	if (client != INVALID_SOCKET)
		closesocket(client);
	if (pending != INVALID_SOCKET && pending != client)
		closesocket(pending);
}

int SocksHelper::TCP::Send(const char* buffer, int length)
{
	if (buffer == nullptr || length <= 0)
		return SOCKET_ERROR;

	SOCKET client = INVALID_SOCKET;
	if (!BeginSocketOperation(client))
		return SOCKET_ERROR;
	OperationLease operation(*this);
	return send(client, buffer, length, 0);
}

int SocksHelper::TCP::Read(char* buffer, int length)
{
	if (buffer == nullptr || length <= 0)
		return SOCKET_ERROR;

	SOCKET client = INVALID_SOCKET;
	if (!BeginSocketOperation(client))
		return SOCKET_ERROR;
	OperationLease operation(*this);
	return recv(client, buffer, length, 0);
}

bool SocksHelper::TCP::BeginSocketOperation(SOCKET& socket)
{
	lock_guard<mutex> lock(this->stateLock);
	if (this->stopping || this->tcpSocket == INVALID_SOCKET)
		return false;

	socket = this->tcpSocket;
	++this->activeOperations;
	return true;
}

void SocksHelper::TCP::EndSocketOperation()
{
	lock_guard<mutex> lock(this->stateLock);
	--this->activeOperations;
	if (this->activeOperations == 0)
		this->stateChanged.notify_all();
}

SocksHelper::TCP::OperationLease::OperationLease(TCP& owner) noexcept : owner(owner)
{
}

SocksHelper::TCP::OperationLease::~OperationLease()
{
	owner.EndSocketOperation();
}

SocksHelper::UDP::~UDP()
{
	Stop();
}

bool SocksHelper::UDP::Associate()
{
	{
		lock_guard<mutex> lock(this->stateLock);
		if (this->stopping)
			return false;
		if (this->tcpSocket != INVALID_SOCKET)
			return true;

		++this->activeOperations;
	}
	OperationLease operation(*this);

	SOCKET client = SocksHelper::Connect();
	SOCKADDR_IN6 relay{};
	bool associated = false;

	do
	{
		if (client == INVALID_SOCKET)
			break;

		DWORD timeout = 4000;
		setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
		setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));

		if (!SocksHelper::Handshake(client))
			break;

		char buffer[10]{};
		buffer[0] = 0x05;
		buffer[1] = 0x03;
		buffer[3] = 0x01;

		if (!SendAll(client, buffer, 10))
		{
			printf("[Redirector][SocksHelper::UDP::Associate] Send udp associate request failed: %d\n", WSAGetLastError());
			break;
		}

		if (!ReceiveExact(client, buffer, 3))
		{
			printf("[Redirector][SocksHelper::UDP::Associate] Receive udp associate response failed: %d\n", WSAGetLastError());
			break;
		}

		if (buffer[1] != 0x00)
		{
			printf("[Redirector][SocksHelper::UDP::Associate] UDP associate failed: %d\n", buffer[1]);
			break;
		}

		if (!SocksHelper::SplitAddr(client, &relay))
			break;

		associated = true;
	} while (false);

	{
		lock_guard<mutex> lock(this->stateLock);
		if (associated && !this->stopping)
		{
			if (this->tcpSocket == INVALID_SOCKET)
			{
				this->tcpSocket = client;
				this->address = relay;
				client = INVALID_SOCKET;
			}
			associated = true;
		}
		else
		{
			associated = false;
		}

	}

	if (client != INVALID_SOCKET)
		closesocket(client);

	return associated;
}

bool SocksHelper::UDP::CreateUDP()
{
	SOCKADDR_IN6 relay{};
	{
		lock_guard<mutex> lock(this->stateLock);
		if (this->stopping || this->tcpSocket == INVALID_SOCKET)
			return false;
		if (this->udpSocket != INVALID_SOCKET)
			return true;

		relay = this->address;
		++this->activeOperations;
	}
	OperationLease operation(*this);

	SOCKET client = INVALID_SOCKET;
	bool created = false;
	if (relay.sin6_family == AF_INET)
	{
		client = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
		if (client == INVALID_SOCKET)
		{
			printf("[Redirector][SocksHelper::UDP::CreateUDP] Create IPv4 socket failed: %d\n", WSAGetLastError());
		}
		else
		{
			SOCKADDR_IN bindaddr{};
			bindaddr.sin_family = AF_INET;
			if (bind(client, reinterpret_cast<PSOCKADDR>(&bindaddr), sizeof(bindaddr)) == SOCKET_ERROR)
				printf("[Redirector][SocksHelper::UDP::CreateUDP] Listen IPv4 socket failed: %d\n", WSAGetLastError());
			else
				created = true;
		}
	}
	else
	{
		client = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
		if (client == INVALID_SOCKET)
		{
			printf("[Redirector][SocksHelper::UDP::CreateUDP] Create IPv6 socket failed: %d\n", WSAGetLastError());
		}
		else
		{
			SOCKADDR_IN6 bindaddr{};
			bindaddr.sin6_family = AF_INET6;
			if (bind(client, reinterpret_cast<PSOCKADDR>(&bindaddr), sizeof(bindaddr)) == SOCKET_ERROR)
				printf("[Redirector][SocksHelper::UDP::CreateUDP] Listen IPv6 socket failed: %d\n", WSAGetLastError());
			else
				created = true;
		}
	}

	{
		lock_guard<mutex> lock(this->stateLock);
		if (created && !this->stopping)
		{
			if (this->udpSocket == INVALID_SOCKET)
			{
				this->udpSocket = client;
				client = INVALID_SOCKET;
			}
			created = true;
		}
		else
		{
			created = false;
		}

	}

	if (client != INVALID_SOCKET)
		closesocket(client);

	return created;
}

bool SocksHelper::UDP::IsAssociated()
{
	lock_guard<mutex> lock(this->stateLock);
	return !this->stopping && this->tcpSocket != INVALID_SOCKET;
}

bool SocksHelper::UDP::IsReady()
{
	lock_guard<mutex> lock(this->stateLock);
	return !this->stopping && this->tcpSocket != INVALID_SOCKET && this->udpSocket != INVALID_SOCKET;
}

bool SocksHelper::UDP::IsStopped()
{
	lock_guard<mutex> lock(this->stateLock);
	return this->stopping;
}

void SocksHelper::UDP::Stop()
{
	SOCKET control = INVALID_SOCKET;
	SOCKET datagram = INVALID_SOCKET;
	{
		unique_lock<mutex> lock(this->stateLock);
		this->stopping = true;
		this->stateChanged.wait(lock, [this] { return this->activeOperations == 0; });

		control = exchange(this->tcpSocket, INVALID_SOCKET);
		datagram = exchange(this->udpSocket, INVALID_SOCKET);
	}

	if (datagram != INVALID_SOCKET)
		closesocket(datagram);
	if (control != INVALID_SOCKET)
		closesocket(control);
}

bool SocksHelper::UDP::BeginSocketOperation(SOCKET& socket, SOCKET* control, PSOCKADDR_IN6 relay)
{
	lock_guard<mutex> lock(this->stateLock);
	if (this->stopping || this->udpSocket == INVALID_SOCKET)
		return false;

	socket = this->udpSocket;
	if (control != nullptr)
	{
		if (this->tcpSocket == INVALID_SOCKET)
			return false;
		*control = this->tcpSocket;
	}
	if (relay != nullptr)
		*relay = this->address;

	++this->activeOperations;
	return true;
}

void SocksHelper::UDP::EndSocketOperation()
{
	lock_guard<mutex> lock(this->stateLock);
	--this->activeOperations;
	if (this->activeOperations == 0)
		this->stateChanged.notify_all();
}

SocksHelper::UDP::OperationLease::OperationLease(UDP& owner) noexcept : owner(owner)
{
}

SocksHelper::UDP::OperationLease::~OperationLease()
{
	owner.EndSocketOperation();
}

int SocksHelper::UDP::Send(PSOCKADDR_IN6 target, const char* buffer, int length)
{
	if (target == nullptr || buffer == nullptr || length < 0)
		return SOCKET_ERROR;

	if (target->sin6_family != AF_INET && target->sin6_family != AF_INET6)
		return SOCKET_ERROR;

	vector<char> data(3 + 1 + 16 + 2 + static_cast<size_t>(length));
	data[3] = (target->sin6_family == AF_INET) ? 0x01 : 0x04;

	if (target->sin6_family == AF_INET)
	{
		auto ipv4 = (PSOCKADDR_IN)target;

		memcpy(data.data() + 4, &ipv4->sin_addr, 4);
		memcpy(data.data() + 8, &ipv4->sin_port, 2);
	}
	else
	{
		memcpy(data.data() + 4, &target->sin6_addr, 16);
		memcpy(data.data() + 20, &target->sin6_port, 2);
	}

	memcpy(data.data() + 3 + 1 + (target->sin6_family == AF_INET ? 4 : 16) + 2, buffer, length);
	auto dataLength = 3 + 1 + (target->sin6_family == AF_INET ? 4 : 16) + 2 + length;

	SOCKET socket = INVALID_SOCKET;
	SOCKADDR_IN6 relay{};
	if (!BeginSocketOperation(socket, nullptr, &relay))
		return SOCKET_ERROR;
	OperationLease operation(*this);

	auto sent = sendto(socket, data.data(), dataLength, 0, reinterpret_cast<PSOCKADDR>(&relay),
		relay.sin6_family == AF_INET ? sizeof(SOCKADDR_IN) : sizeof(SOCKADDR_IN6));
	const int socketError = sent == dataLength ? 0 : WSAGetLastError();
	if (sent != dataLength)
	{
		printf("[Redirector][SocksHelper::UDP::Send] Send packet failed: %d\n", socketError);
		return SOCKET_ERROR;
	}

	return length;
}

int SocksHelper::UDP::Read(PSOCKADDR_IN6 target, char* buffer, int length, PTIMEVAL timeout)
{
	if (buffer == nullptr || length <= 0)
		return SOCKET_ERROR;

	SOCKET socket = INVALID_SOCKET;
	SOCKET control = INVALID_SOCKET;
	if (!BeginSocketOperation(socket, &control))
		return SOCKET_ERROR;
	OperationLease operation(*this);

	timeval wait{};
	wait.tv_usec = 200000;
	if (timeout != nullptr)
	{
		auto requested = static_cast<long long>(timeout->tv_sec) * 1000000LL + timeout->tv_usec;
		if (requested < 0)
			requested = 0;
		if (requested < 200000)
		{
			wait.tv_sec = static_cast<long>(requested / 1000000);
			wait.tv_usec = static_cast<long>(requested % 1000000);
		}
	}

	fd_set fds;
	FD_ZERO(&fds);
	FD_SET(socket, &fds);
	FD_SET(control, &fds);
	int ready = select(0, &fds, nullptr, nullptr, &wait);
	if (ready == 0 || ready == SOCKET_ERROR)
		return ready;

	if (FD_ISSET(control, &fds))
		return SOCKET_ERROR;

	int size = recvfrom(socket, buffer, length, 0, nullptr, nullptr);
	if (size <= 0)
		return size;

	if (size < 4 || static_cast<unsigned char>(buffer[0]) != 0 ||
		static_cast<unsigned char>(buffer[1]) != 0 || static_cast<unsigned char>(buffer[2]) != 0)
		return SOCKET_ERROR;

	SOCKADDR_IN6 addr{};
	int headerLength = 0;
	if (static_cast<unsigned char>(buffer[3]) == 0x01)
	{
		if (size < 10)
			return SOCKET_ERROR;

		auto ipv4 = (PSOCKADDR_IN)&addr;
		ipv4->sin_family = AF_INET;

		memcpy(&ipv4->sin_addr, buffer + 4, 4);
		memcpy(&ipv4->sin_port, buffer + 8, 2);
		headerLength = 10;
	}
	else if (static_cast<unsigned char>(buffer[3]) == 0x04)
	{
		if (size < 22)
			return SOCKET_ERROR;

		addr.sin6_family = AF_INET6;

		memcpy(&addr.sin6_addr, buffer + 4, 16);
		memcpy(&addr.sin6_port, buffer + 20, 2);
		headerLength = 22;
	}
	else
	{
		return SOCKET_ERROR;
	}

	if (target != NULL)
		memcpy(target, &addr, sizeof(SOCKADDR_IN6));

	auto payloadLength = size - headerLength;
	memmove(buffer, buffer + headerLength, static_cast<size_t>(payloadLength));
	return payloadLength;
}
