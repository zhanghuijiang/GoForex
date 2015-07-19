//+------------------------------------------------------------------+
//|                                                  Server Emulator |
//|                   Copyright 2001-2014, MetaQuotes Software Corp. |
//|                                        http://www.metaquotes.net |
//+------------------------------------------------------------------+
#pragma once

#include "Processor.h"

const int BUFFER_SIZE = 4096;

enum eOrderSide
{
	BUY,
	SELL,
	UNKNOWN
};

enum eReturnCode
{
	E_InvalidReturnCode = -1,
	E_Success = 100,
	E_Failure = 101
};

enum eMessageType
{
	E_InvalidMessageType = -1,
	
	E_Login = 10000,
	E_OpenOrder,
	E_CloseOrder,
	E_TradesAdd,
	E_GetTrades,
	E_GetTradesReturn,
	E_CreateUser,
	E_GetUserInfo,
	E_GetServerTime,


	E_ReturnCode = 20000,
	E_OrderReturnCode,
};

enum eGetOrderType
{
	E_InvalidOrderType = -1,

	E_OpenOrders = 1,
	E_CloseOrders = 2,
	E_AllOrders = 4
};

struct SOrderInfo
{
	int Ticket;
	int Login;
	__time32_t Time;
	char Symbol[12];
	eOrderSide Side;
	int Volume;
	double Price;
	int NewTicket; //for partial close
	char Comment[32];
	int State; // enum { TS_OPEN_NORMAL, TS_OPEN_REMAND, TS_OPEN_RESTORED, TS_CLOSED_NORMAL, TS_CLOSED_PART, TS_CLOSED_BY, TS_DELETED };
	char Group[16];
};

struct SUserDetails
{
	int Login;
	char Group[16];
	int Leverage;
	double Balance;
	double Credit;
	char Name[128];
};

struct SBase
{
	SBase(eMessageType messageType)
	{
		MessageType = messageType;
		ServerTime = time(NULL);
		ClientTime = 0;
	}

	eMessageType MessageType;
	__time32_t ServerTime;
	LONG64 ClientTime;
};

struct SReturnCode : public SBase
{
	SReturnCode() : SBase(E_ReturnCode)
	{
		CalledMessageType = E_InvalidMessageType;
		ReturnCode = E_InvalidReturnCode;
		ZeroMemory(ErrorDescription, sizeof(ErrorDescription));
	}

	eMessageType CalledMessageType;
	eReturnCode  ReturnCode;
	char         ErrorDescription[64];
};

struct SOrderReturnCode : public SReturnCode
{
	SOrderReturnCode()
	{
		MessageType = E_OrderReturnCode;
		OrderID = 0;
		IsPartial = 0;
	}
	int OrderID;
	int IsPartial;
};

struct SLogin : public SBase
{
	SLogin() : SBase(E_Login)
	{
		ZeroMemory(User, sizeof(User));
		ZeroMemory(Password, sizeof(Password));
	}

	char User[32];
	char Password[32];
};

struct SOpenOrder : public SBase
{
	SOpenOrder() : SBase(E_OpenOrder)
	{
		ZeroMemory(&OrderInfo, sizeof(OrderInfo));
	}
	SOrderInfo OrderInfo;
};

struct SCloseOrder : public SBase
{
	SCloseOrder() : SBase(E_CloseOrder)
	{
		ZeroMemory(&OrderInfo, sizeof(OrderInfo));
	}
	SOrderInfo OrderInfo;
};

struct STradeAdd : public SBase
{
	STradeAdd() : SBase(E_TradesAdd)
	{
		ZeroMemory(&OrderInfo, sizeof(OrderInfo));
	}
	SOrderInfo OrderInfo;
};

struct SGetTrades : public SBase
{
	SGetTrades() : SBase(E_GetTrades)
	{
		tm from;
		from.tm_year = 1970;
		from.tm_mon = 1;
		from.tm_mday = 1;
		from.tm_hour = 0;
		from.tm_min = 0;
		from.tm_sec = 0;
		From = mktime(&from);

		To = time(NULL);
		login = 0;
		GetOrderType = E_InvalidOrderType;
		ZeroMemory(Group, sizeof(Group));
	}
	
	__time32_t From;
	__time32_t To;
	int  login;
	eGetOrderType GetOrderType;
	char Group[16];
};

struct SGetTradesReturn : public SGetTrades
{
	SGetTradesReturn(SGetTrades& Obj)
	{
		MessageType = E_GetTradesReturn;
		ClientTime = Obj.ClientTime;
		Total = 0;
		From = Obj.From;
		To = Obj.To;
		login = Obj.login;
		GetOrderType = Obj.GetOrderType;
	}
	int Total;
};

struct SCreateUser : public SBase
{
	SCreateUser() : SBase(E_CreateUser)
	{
		Login = 0;
		Deposit = 0;
		ZeroMemory(Group, sizeof(Group));
		ZeroMemory(Password, sizeof(Password));
		ZeroMemory(Name, sizeof(Name));
	}

	int Login;
	char Group[16];
	char Password[16];
	char Name[128];
	int Deposit;
};

struct SGetUserInfoReq : public SBase
{
	SGetUserInfoReq() : SBase(E_GetUserInfo)
	{
		Login = 0;
	}
	int Login;
};

struct SGetUserInfo : public SReturnCode
{
	SGetUserInfo()
	{
		MessageType = E_GetUserInfo;
		ZeroMemory(Group, sizeof(Group));
		ZeroMemory(Name, sizeof(Name));
		Login = 0;
		Leverage = 0;
		Balance = 0;
		Credit = 0;
	}

	int Login;
	char Group[16];
	char Name[128];
	int Leverage;
	double Balance;
	double Credit;
};

struct SGetServerTime : SBase
{
	SGetServerTime() : SBase(E_GetServerTime)
	{
	}
};

//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
struct ServerCfg
{
	char              password_quotes[32];     // password for "LOGIN QUOTES pass"
	char              password_trades[32];     // password for "LOGIN TRADES pass"
};
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
class CSocketClient
{
	friend class CSocketServer;
public:
	volatile int      m_finished;                // stop flag

protected:
	SOCKET            m_socket;                  // network socket
	HANDLE            m_thread;                  // thread handle
	//volatile int      m_finished;                // stop flag
	ServerCfg        *m_cfg;                     // link to server configuration (read only)
	CSocketClient    *m_next;                    // link to next connection
	ULONG             m_ip;
	CProcessor       *m_Processor;
	CSync             m_sync;           // synchronizer

public:
	CSocketClient();
	virtual          ~CSocketClient() { Shutdown(); }

	virtual void      Shutdown(void);
	void              Close(void);
	int               Finished(void)            { return(m_finished); }
	CSocketClient*    Next(void)                { return(m_next); }
	void              Next(CSocketClient *next) { m_next = next; }

	int               StartClient(const SOCKET sock, ULONG ip, ServerCfg *cfg, CProcessor* processor);

protected:
	void              PumpQuotes(void);
	void              PumpTrades(void);
	int               IsReadible();
	int               ReadString(char *buf, int maxlen);
	int               SendString(char *str, int len);
	int               ProcessTrade(char *str);
	virtual void      ThreadProcess(void);
	static UINT __stdcall ThreadWrapper(LPVOID pParam);
};
extern CProcessor ExtProcessor;
//+------------------------------------------------------------------+
