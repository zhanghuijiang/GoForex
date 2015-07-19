//+------------------------------------------------------------------+
//|                                                  Server Emulator |
//|                   Copyright 2001-2014, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#include "stdafx.h"
#include <time.h>
#include "SocketClient.h"

extern CServerInterface *ExtServer;

//---
char *ServerPassword = "MetaQuotes";
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
CSocketClient::CSocketClient() :m_socket(INVALID_SOCKET), m_thread(NULL), m_finished(FALSE), m_cfg(NULL), m_next(NULL), m_Processor(NULL)
{
	//---
	//---
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketClient::Shutdown(void)
{
	//--- lets close the connection
	m_finished = TRUE;
	if (m_socket != INVALID_SOCKET) Sleep(500);
	Close();
	//--- wait until thread finished
	if (m_thread != NULL)
	{
		WaitForSingleObject(m_thread, INFINITE);
		CloseHandle(m_thread);
		m_thread = NULL;
	}
	//---
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketClient::Close(void)
{
	//---
	if (m_socket != INVALID_SOCKET)
	{
		shutdown(m_socket, 2);
		closesocket(m_socket);
		m_socket = INVALID_SOCKET;
	}
	//---
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
int CSocketClient::IsReadible()
{
	unsigned long size = 0;
	//---
	if (m_socket != INVALID_SOCKET)
	if (ioctlsocket(m_socket, FIONREAD, &size) != 0) { Close(); size = 0; }
	//---
	return(size);
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
//int CSocketClient::ReadString(char *buf, int maxlen)
//{
//	char *cp = buf;
//	int   res, count = 0;
//	//--- 
//	if (m_socket == INVALID_SOCKET || buf == NULL || maxlen<0) return(FALSE);
//	//---  
//	while (maxlen>0)
//	{
//		if ((res = recv(m_socket, cp, 1, 0))<1)
//		{
//			if (WSAGetLastError() != WSAEWOULDBLOCK) break;
//			Sleep(50);
//			if (++count>5) break;
//			continue;
//		}
//		if (*cp == 13) continue;
//		if (*cp == 10) { maxlen = 0; break; }
//		maxlen -= res; cp += res;
//	}
//	*cp = 0; // terminator
//	//---
//	return(maxlen <= 0);
//}
int CSocketClient::ReadString(char *buf, int maxlen)
{
	//CSingleLock lock(&m_sync);
	char *cp = buf;
	int   res, count = 0;
	//--- 
	if (m_socket == INVALID_SOCKET || buf == NULL || maxlen<=0) return(FALSE);
	//---  
	while (maxlen>0)
	{
		res = recv(m_socket, cp, __min(maxlen, BUFFER_SIZE), 0);
		if (res == SOCKET_ERROR)
		{
			if (WSAGetLastError() != WSAEWOULDBLOCK)
			{
				Close();
				m_finished = TRUE;
				return FALSE;
				//break;
			}
			Sleep(50);
			//if (++count>5) break;
			//continue;
		}
		else if (res == 0) // Connection closed
		{
			Close();
			m_finished = TRUE;
			return FALSE;
		}

		//if (*cp == 13) continue;
		//if (*cp == 10) { maxlen = 0; break; }
		maxlen -= res; cp += res;
	}
	//*cp = 0; // terminator
	//---
	return(maxlen <= 0);
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
int CSocketClient::SendString(char *str, int len)
{
	//CSingleLock lock(&m_sync);
	DWORD  bytes;
	//--- check parameters
	if (m_socket == INVALID_SOCKET || str == NULL || len<0) return(FALSE);
	//---
	if (len == 0) len = strlen(str);
	if (len>0)
	{
		WSABUF buf = { len, str };
		if (WSASend(m_socket, &buf, 1, &bytes, 0, NULL, NULL) != 0) { Close(); return(FALSE); }
	}
	//---
	return(TRUE);
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
int CSocketClient::StartClient(const SOCKET sock, ULONG ip, ServerCfg *cfg, CProcessor* processor)
{
	UINT  id = 0;
	//--- check parameters
	if (ip == NULL || cfg == NULL || processor == NULL) return(FALSE);
	//--- close before start
	Shutdown();
	m_finished = FALSE;
	m_ip = ip;
	//--- save socket handle and start thread
	m_socket = sock;
	m_cfg = cfg;
	m_Processor = processor;
	if (m_thread == NULL)
		m_thread = (HANDLE)_beginthreadex(NULL, 256000, ThreadWrapper, (void*)this, 0, &id);
	//--- all is ok
	return(TRUE);
}
//+------------------------------------------------------------------+
//| Thread wrapper                                                   |
//+------------------------------------------------------------------+
UINT __stdcall CSocketClient::ThreadWrapper(LPVOID pParam)
{
	//---
	if (pParam != NULL) ((CSocketClient*)pParam)->ThreadProcess();
	//---
	return(0);
}
const char* LOGIN_CMD = "FXLOGIN";
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketClient::ThreadProcess(void)
{
	eMessageType message_type = E_InvalidMessageType;
	char   tmp[BUFFER_SIZE] = "";
	//--- read first line for login
	while (m_finished == FALSE && m_cfg != NULL && m_Processor != NULL)
	{
		if (ReadString(tmp, sizeof(eMessageType)) == TRUE)
		{
			message_type = *(eMessageType*)tmp;
		}
		if (message_type == E_Login)
		{
			if (ReadString(tmp + sizeof(eMessageType), sizeof(SLogin)-sizeof(eMessageType)) == TRUE)
			{
				SLogin* obj = (SLogin*)tmp;
				SReturnCode& ret = m_Processor->CheckLogin((SLogin*)tmp);
				SendString((char*)&ret, sizeof(ret));
			}
		}
		else if (message_type == E_OpenOrder)
		{
			if (ReadString(tmp + sizeof(eMessageType), sizeof(SOpenOrder)-sizeof(eMessageType)) == TRUE)
			{
				SOpenOrder* obj = (SOpenOrder*)tmp;
				//SOrderReturnCode& ret = m_Processor->OrdersOpen(obj->OrderInfo.Login, obj->OrderInfo.Side, obj->OrderInfo.Symbol, obj->OrderInfo.Price, obj->OrderInfo.Volume, obj->OrderInfo.Comment, this);
				SOrderReturnCode& ret = m_Processor->OrdersOpen(*obj);
				SendString((char*)&ret, sizeof(ret));
			}
		}
		else if (message_type == E_CloseOrder)
		{
			if (ReadString(tmp + sizeof(eMessageType), sizeof(SCloseOrder)-sizeof(eMessageType)) == TRUE)
			{
				SCloseOrder* obj = (SCloseOrder*)tmp;
				//SOrderReturnCode& ret = m_Processor->OrdersClose(obj->OrderInfo.Ticket, obj->OrderInfo.Volume, obj->OrderInfo.Price, obj->OrderInfo.Comment, this);
				SOrderReturnCode& ret = m_Processor->OrdersClose(*obj);
				SendString((char*)&ret, sizeof(ret));
			}
		}
		else if (message_type == E_GetTrades)
		{
			if (ReadString(tmp + sizeof(eMessageType), sizeof(SGetTrades)-sizeof(eMessageType)) == TRUE)
			{
				SGetTrades* obj = (SGetTrades*)tmp;
				SGetTradesReturn ret_code(*obj);

				STradeAdd* ret = m_Processor->GetTrades(*obj, ret_code.Total);

				//if (obj->login == 0 && (obj->Group[0] == 0 || strlen(obj->Group) == 0)) // Get all trades for all logins if login == 0 and group is empty
				//	ret = m_Processor->GetAllTrades(obj->From, obj->To, obj->GetOrderType, ret_code.Total);
				//else if (obj->Group[0] == 0 || strlen(obj->Group) == 0)
				//	ret = m_Processor->GetTrades(obj->From, obj->To, obj->login, obj->GetOrderType, ret_code.Total);
				//else
				//	ret = m_Processor->GetTrades(obj->Group, obj->From, obj->To, obj->GetOrderType, ret_code.Total);

				SendString((char*)&ret_code, sizeof(ret_code));
				if (ret_code.Total > 0)
				{
					SendString((char*)ret, sizeof(*ret) * ret_code.Total);
					delete [] ret;
				}
			}
		}
		else if (message_type == E_CreateUser)
		{
			if (ReadString(tmp + sizeof(eMessageType), sizeof(SCreateUser)-sizeof(eMessageType)) == TRUE)
			{
				SCreateUser* obj = (SCreateUser*)tmp;
				SReturnCode& ret = m_Processor->CreateUser(*obj);
				SendString((char*)&ret, sizeof(ret));
			}
		}
		else if (message_type == E_GetUserInfo)
		{
			if (ReadString(tmp + sizeof(eMessageType), sizeof(SGetUserInfoReq)-sizeof(eMessageType)) == TRUE)
			{
				SGetUserInfoReq* obj = (SGetUserInfoReq*)tmp;
				SGetUserInfo ui;
				m_Processor->GetUserInfo(obj, &ui);
				SendString((char*)&ui, sizeof(ui));
			}
		}
		else  if (message_type == E_GetServerTime)
		{
			if (ReadString(tmp + sizeof(eMessageType), sizeof(SGetServerTime)-sizeof(eMessageType)) == TRUE)
			{
				SGetServerTime* obj = (SGetServerTime*)tmp;
				SGetServerTime ret;
				ret.ClientTime = obj->ClientTime;
				SendString((char*)&ret, sizeof(ret));
			}
		}
		else
		{
			SReturnCode ret;
			ret.CalledMessageType = message_type;
			sprintf(ret.ErrorDescription, "Invalid message type");
			SendString((char*)&ret, sizeof(ret));
		}
	}
	ExtServer->LogsOut(CmdOK, "FXPlugin", "CSocketClient finished");
	//--- send EOF, sleep a little and close connection
	//if (SendString("EOF connection closed\r\n", 0) == TRUE) Sleep(1000);
	Close();
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketClient::PumpQuotes(void)
{
	//   char       tmp[256]="";
	//   CSource    gen;
	//   TickInfo   inf;
	//   time_t     ctm;
	//   tm        *ttm;
	////--- send confirmation
	//   SendString("LOGIN: OK\r\n",0);
	//   printf("Quotes: pumping\n");
	////---
	//   while(m_finished==FALSE)
	//     {
	//      //--- prepare new tick info
	//      gen.ReadTick(&inf);                  // generate new tick
	//      ctm=time(NULL)+2*60*60*24;           // simple and dirty value date after 2 days
	//      if((ttm=gmtime(&ctm))==NULL) break;  // convert time
	//      //--- QUOTE symbol, bid, ask, expiry time in sec, valueDate, rateId
	//      //--- QUOTE USDSGD, 1.6000, 1.6003, 6, 2004-12-21, ijwfx9873409-909
	//      _snprintf(tmp,sizeof(tmp)-1,"QUOTE %s, %.5lf, %.5lf, %d, %04d-%02d-%02d, %d-%d\r\n",
	//                    inf.security,inf.bid,inf.ask,inf.timerel,
	//                    1900+ttm->tm_year,1+ttm->tm_mon,ttm->tm_mday,ctm,rand()&255);
	//      if(SendString(tmp,0)==FALSE) break;
	//     }
	//---
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CSocketClient::PumpTrades(void)
{
	char tmp[256];
	//--- send confirmation
	SendString("LOGIN: OK\r\n", 0);
	printf("Trades: waiting for commands\n");
	//---
	while (m_finished == FALSE)
	{
		if (ReadString(tmp, sizeof(tmp)-1) == FALSE) break;
		printf("Trade: %s\n", tmp);
		if (_stricmp(tmp, "LOGOUT") == 0)  break;
		if (_strnicmp(tmp, "TRADE", 5) == 0) if (ProcessTrade(tmp) == FALSE) break;
	}
	//---
}
//+------------------------------------------------------------------+
//| We just return clients request and add "OK" or "FAIL"            |
//+------------------------------------------------------------------+
int CSocketClient::ProcessTrade(char *str)
{
	char answer[512], *cp, *ep;
	//---
	printf("%s\n", str);
	//--- parse
	// in:  TRADE id, login, cmd, volume, symbol, price, bid/ask, value_date, rate_id
	// out: REPLY id, login, cmd, volume, symbol, price, bid/ask, rate-id, status,message
	//--- find status...
	if ((ep = strchr(str, ',')) == NULL)   return(FALSE); // id
	if ((cp = strchr(ep + 1, ',')) == NULL)  return(FALSE); // login
	if ((ep = strchr(cp + 1, ',')) == NULL)  return(FALSE); // cmd
	if ((cp = strchr(ep + 1, ',')) == NULL)  return(FALSE); // volume
	if ((ep = strchr(cp + 1, ',')) == NULL)  return(FALSE); // symbol
	if ((cp = strchr(ep + 1, ',')) == NULL)  return(FALSE); // price
	if ((ep = strchr(cp + 1, ',')) == NULL)  return(FALSE); // bid/ask
	*ep = 0;
	//---
	COPY_STR(answer, str);
	//--- parse rate-id
	if ((cp = strchr(ep + 1, ',')) == NULL)  return(FALSE); // value-date
	*cp = 0;
	strcat(answer, ", "); strcat(answer, cp + 2);
	//--- add status
	strcat(answer, ", "); strcat(answer, (rand() % 2) ? ("OK") : ("FAIL"));
	strcat(answer, ", \r\n");
	memcpy(answer, "REPLY", 5);
	//---
	printf("%s\n", answer);
	return SendString(answer, strlen(answer));
}
//+------------------------------------------------------------------+
