//+------------------------------------------------------------------+
//|                                                  Server Emulator |
//|                   Copyright 2001-2014, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#include "stdafx.h"
#include "SocketServer.h"
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
CSocketServer::CSocketServer(CProcessor& processor) : CSocketClient(), m_connections(NULL), m_Processor(processor)
{
	//--- config by default
	ZeroMemory(&m_cfg, sizeof(m_cfg));
	COPY_STR(m_cfg.password_quotes, "PASSWORD");
	COPY_STR(m_cfg.password_trades, "PASSWORD");
	//---
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
CSocketServer::~CSocketServer(void) { Shutdown(); }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketServer::Shutdown(void)
{
	//--- stop main server
	CSocketClient::Shutdown();
	//--- close all  connections
	while (m_connections != NULL)
	{
		CSocketClient *next = m_connections->Next();
		delete m_connections;
		m_connections = next;
	}
	//---
}
//+------------------------------------------------------------------+
//| Read configuration                                               |
//+------------------------------------------------------------------+
void CSocketServer::Initialize(void)
{
	char  tmp[256], *cp;
	FILE *in;
	//--- construct config filename
	GetModuleFileName(NULL, tmp, sizeof(tmp)-1);
	if ((cp = strrchr(tmp, '.')) != NULL) *cp = 0;
	strcat(tmp, ".cfg");
	//--- open file and read all lines
	if ((in = fopen(tmp, "rt")) != NULL)
	{
		while (fgets(tmp, 250, in) != NULL)
		{
			ClearLF(tmp);
			if (tmp[0] == ';') continue;
			//--- parse lines
			if (GetStrParam(tmp, "PasswordQuotes=", m_cfg.password_quotes, sizeof(m_cfg.password_quotes) - 1) == TRUE) continue;
			if (GetStrParam(tmp, "PasswordTrades=", m_cfg.password_trades, sizeof(m_cfg.password_trades) - 1) == TRUE) continue;
		}
		fclose(in);
	}
	//---
}
//+------------------------------------------------------------------+
//| Start server for incoming connections                            |
//+------------------------------------------------------------------+
int CSocketServer::StartServer(const int port)
{
	UINT  id = 0;
	//--- close before start
	Shutdown();
	m_finished = FALSE;
	//--- allocate socket
	if ((m_socket = socket(AF_INET, SOCK_STREAM, 0)) == INVALID_SOCKET)
	{
		printf("SocketServer: invalid socket (WSAStartup is missing?)\n");
		return(FALSE);
	}
	//--- setup IP and port
	SOCKADDR_IN sa;
	ZeroMemory(&sa, sizeof(sa));
	sa.sin_port = htons(port);
	sa.sin_family = AF_INET;
	sa.sin_addr.s_addr = INADDR_ANY;
	//--- associate the local address with m_socket.
	if (bind(m_socket, (LPSOCKADDR)&sa, sizeof(sa)) == SOCKET_ERROR)
	{
		Close();
		printf("SocketServer: error binding to port %d\n", port);
		return(FALSE);
	}
	//--- establish a socket to listen for incoming connections.
	if (listen(m_socket, 5) == SOCKET_ERROR)
	{
		Close();
		printf("SocketServer: socket listening error\n");
		return(FALSE);
	}
	//--- start thread
	if (m_thread == NULL)
		m_thread = (HANDLE)_beginthreadex(NULL, 256000, ThreadWrapper, (void*)this, 0, &id);
	//--- all is ok
	printf("SocketServer: listening on port %d\n", port);
	return(TRUE);
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketServer::ThreadProcess(void)
{
	SOCKET         sock;
	SOCKADDR_IN    sin;
	int            sin_len = sizeof(sin);
	char          *ip;
	CSocketClient *user;
	//--- loop until finished
	while (m_finished == FALSE)
	{
		//--- accept an incoming connection
		if ((sock = accept(m_socket, (struct sockaddr *)&sin, (int *)&sin_len)) == INVALID_SOCKET) break;
		//--- check IP address
		if ((ip = inet_ntoa(sin.sin_addr)) == NULL)
		{
			printf("SocketServer: IP resolving failed\n");
			closesocket(sock);
			continue;
		}
		
		//if (!m_Processor.IsIPExists(sin.sin_addr.S_un.S_addr))
		//{
		//	printf("SocketServer: IP not allowed\n");
		//	closesocket(sock);
		//	continue;
		//}

		//--- allocate connection
		printf("SocketServer: %s connected\n", ip);
		if ((user = new CSocketClient) == NULL) closesocket(sock);
		else
		{
			//--- add to list
			user->Next(m_connections);
			m_connections = user;
			//--- start
			user->StartClient(sock, sin.sin_addr.S_un.S_addr, &m_cfg, &m_Processor);
		}
	}
	//---
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketServer::SendAll(STradeAdd* tradesAdd)
{
	CSocketClient *client = m_connections;
	while (client != NULL)
	{
		m_Processor.Out(CmdTrade, "FXPlugin::Start Send E_TradesAdd message to ", "%d", client->m_ip);
		client->SendString((char*)tradesAdd, sizeof(*tradesAdd));
		m_Processor.Out(CmdTrade, "FXPlugin::End Send E_TradesAdd message to ", "%d", client->m_ip);
		client = client->Next();
	}
}
