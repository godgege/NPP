#pragma once
#ifndef SOCKSHELPER_H
#define SOCKSHELPER_H
#include "Based.h"

namespace SocksHelper
{
	SOCKET Connect();
	bool Handshake(SOCKET client);
	bool SplitAddr(SOCKET client, PSOCKADDR_IN6 addr);

	typedef class TCP
	{
	public:
		TCP() = default;
		~TCP();
		TCP(const TCP&) = delete;
		TCP& operator=(const TCP&) = delete;

		bool Connect(PSOCKADDR_IN6 target);
		void RequestStop();
		void Stop();

		int Send(const char* buffer, int length);
		int Read(char* buffer, int length);

	private:
		class OperationLease
		{
		public:
			explicit OperationLease(TCP& owner) noexcept;
			~OperationLease();

			OperationLease(const OperationLease&) = delete;
			OperationLease& operator=(const OperationLease&) = delete;

		private:
			TCP& owner;
		};

		bool BeginSocketOperation(SOCKET& socket);
		void EndSocketOperation();

		mutex stateLock;
		condition_variable stateChanged;
		bool stopping = false;
		size_t activeOperations = 0;
		SOCKET tcpSocket = INVALID_SOCKET;
		SOCKET pendingSocket = INVALID_SOCKET;
	} *PTCP;

	typedef class UDP
	{
	public:
		~UDP();
		UDP() = default;
		UDP(const UDP&) = delete;
		UDP& operator=(const UDP&) = delete;

		bool Associate();
		bool CreateUDP();
		bool IsAssociated();
		bool IsReady();
		bool IsStopped();
		void Stop();

		int Send(PSOCKADDR_IN6 target, const char* buffer, int length);
		int Read(PSOCKADDR_IN6 target, char* buffer, int length, PTIMEVAL timeout);

	private:
		class OperationLease
		{
		public:
			explicit OperationLease(UDP& owner) noexcept;
			~OperationLease();

			OperationLease(const OperationLease&) = delete;
			OperationLease& operator=(const OperationLease&) = delete;

		private:
			UDP& owner;
		};

		bool BeginSocketOperation(SOCKET& socket, SOCKET* control = nullptr, PSOCKADDR_IN6 relay = nullptr);
		void EndSocketOperation();

		mutex stateLock;
		condition_variable stateChanged;
		bool stopping = false;
		size_t activeOperations = 0;
		SOCKET tcpSocket = INVALID_SOCKET;
		SOCKET udpSocket = INVALID_SOCKET;

		SOCKADDR_IN6 address = { 0 };
	} *PUDP;
};

#endif
