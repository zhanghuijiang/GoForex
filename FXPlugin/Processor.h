#pragma once
const int _MAX_IPS = 10;

extern struct SLogin;
extern struct SReturnCode;
extern struct SOrderReturnCode;
extern struct SOrderInfo;
extern struct STradeAdd;
extern struct SCreateUser;
extern struct SGetUserInfo;
extern struct SOpenOrder;
extern struct SCloseOrder;
extern struct SGetTrades;
extern struct SGetUserInfoReq;
extern enum eGetOrderType;

class CProcessor
{
	friend class CSocketServer;
	friend class CSocketClient;

private:
	ULONG             m_arrIP[_MAX_IPS];
	char              m_user[32];       // user name
	char              m_password[32];   // password
	ULONG             m_ip;             // allowed IP
	int               m_login;          // login
	char              m_symbol[32];     // symbol
	int               m_volume;         // order volume
	CSync             m_sync;           // synchronizer
	WSADATA           wsa;
	HANDLE            m_threadServer;    // thread handle
	CSocketServer    *m_socketServer;

protected:
	virtual void      ThreadProcess(void);
	static UINT __stdcall ThreadWrapper(LPVOID pParam);

public:
	CProcessor();
	~CProcessor();
	//---- initializing
	void Initialize(void);

	void Terminate(void);
	
	//--- prepare user info
	int	UserInfoGet(const int login, UserInfo *info);

	// Position Opening
	//=================
	// OrdersOpen is a wrapper over OrdersAdd, which additionally implements margin check prior to opening a new position.
	// These checks are performed by a server when a client opens a position. Depending on the logics of a plug-in, part of checks or even all of them can be omitted.
	SOrderReturnCode OrdersOpen(const int login, const int cmd, LPCTSTR symbol, const double open_price, const int volume, LPCTSTR comment, CSocketClient* socket = NULL);
	SOrderReturnCode OrdersOpen(SOpenOrder& Order);
	int OrdersAddOpen(const int login, const int cmd, LPCTSTR symbol, const double open_price, const int volume);
	
	// Position Closing
	//==================
	// OrdersClose differs from OrdersUpdate in the possibility of partial closing.
	// These checks are performed by a server when a client closes a position. Depending on the logics of a plug-in, part of checks or even all of them can be omitted.
	SOrderReturnCode OrdersClose(const int order, const int volume, const double close_price, LPCTSTR comment, CSocketClient* socket = NULL);
	SOrderReturnCode OrdersClose(SCloseOrder& Order);
	SOrderReturnCode OrdersUpdateClose(const int order, const int volume, const double close_price, LPCTSTR comment, CSocketClient* socket = NULL);

	// Placing a Pending Order
	//========================
	// These checks are performed by a server when a client places a pending order. Depending on the logics of a plug-in, part of checks or even all of them can be omitted.
	int OrdersAddOpenPending(const int login, const int cmd, LPCTSTR symbol, const double open_price, const int volume, const time_t expiration);
	
	// Pending Order Activation
	//=========================
	// These checks are performed by a server when an order is activated.Depending on the logics of a plug - in, part of checks or even all of them can be omitted.
	int OrdersUpdateActivate(const int order, const double open_price);

	// Cancellation of a Pending Order
	//================================
	int OrdersUpdateCancel(const int order);

	// Trade hooks
	int SrvTelnet(const ULONG ip, char *buf, const int maxlen);
	void SrvTradesAdd(TradeRecord *trade, const UserInfo *user, const ConSymbol *symbol);
	void SrvTradesAddExt(TradeRecord *trade, const UserInfo *user, const ConSymbol *symbol, const int mode);
	void SrvTradesUpdate(TradeRecord *trade, UserInfo *user, const int mode);
	void SrvTradeRequestApply(RequestInfo *request, const int isdemo);

	//
	void ExamplePositions(void);
	void ExamplePendings(void);


	bool CheckLogin(int login, const char* Password);
	STradeAdd* GetTrades(SGetTrades& getTrades, int& total);
	STradeAdd* GetAllTrades(SGetTrades& getTrades, time_t From, time_t To, eGetOrderType getOrderType, int& total);
	STradeAdd* GetTrades(SGetTrades& getTrades, time_t From, time_t To, int login, eGetOrderType getOrderType, int& total);
	STradeAdd* GetTrades(SGetTrades& getTrades, const char* groups, time_t From, time_t To, eGetOrderType getOrderType, int& total);
	STradeAdd* GetOpenTrades(SGetTrades& getTrades, const char* group, int& total);
	SReturnCode CreateUser(SCreateUser& Obj);
	void GetUserInfo(SGetUserInfoReq* Obj, SGetUserInfo *info);

private:
	//---- out to server log
	void Out(const int code, LPCSTR ip, LPCSTR msg, ...) const;

	SReturnCode CheckLogin(SLogin* Obj);

	// Check basic info
	int CheckBasicInfo(const ULONG ip, char *buffer, const int size);

	// Check if IP is the allowed list
	bool IsIPExists(const ULONG ip)
	{
		ULONG local_ip = inet_addr("127.0.0.1");
		if (ip == local_ip)
			return true;
		for (int ii = 0; ii < sizeof(m_arrIP) / sizeof(m_arrIP[0]); ++ii)
		{
			if (ip == m_arrIP[ii])
				return true;
		}
		return false;
	}
	//
	SOrderInfo& TradeRecordToOrderInfo(TradeRecord& tradeRecord, SOrderInfo& orderInfo);
};
extern CProcessor ExtProcessor;
//+------------------------------------------------------------------+
