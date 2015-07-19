// FXPlugin.cpp : Defines the exported functions for the DLL application.
//

#include "stdafx.h"
#include "Processor.h"
#include "SocketServer.h";
#include <vector>

//#include <map>
//using namespace std;
//typedef std::map<CSocketClient*, INT32> MapClientToOrder;

//---- Link to our server interface
extern CServerInterface *ExtServer;
//---- Our Telnet processor
CProcessor               ExtProcessor;
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
CProcessor::CProcessor(): m_ip(0)
{
	m_user[0] = 0;
	m_password[0] = 0;
	m_login = 4;
	ZeroMemory(m_arrIP, sizeof(m_arrIP));
	ZeroMemory(m_symbol, sizeof(m_symbol));
	strcpy(m_symbol, "EURUSD");
	//m_socketServer = new CSocketServer(*this);
	UINT id = 0;
	m_threadServer = (HANDLE)_beginthreadex(NULL, 256000, ThreadWrapper, (void*)this, 0, &id);

}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
CProcessor::~CProcessor()
{
}
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CProcessor::Initialize(void)
{
	char        buffer[1024];
	int			pos = 0;
	//---- lock
	CSingleLock lock(&m_sync);
	//---- get master password
	ExtConfig.GetString(++pos, "User", m_user, sizeof(m_user)-1, "admin");
	ExtServer->LogsOut(CmdOK, "FXPlugin User", m_user);
	//---- get master password
	ExtConfig.GetString(++pos, "Password", m_password, sizeof(m_password)-1, "admin1234");
	ExtServer->LogsOut(CmdOK, "FXPlugin Password", m_password);
	//---- get allowed IPs
	char ip[10] = "IP ";
	for (int ii = 0; ii < _MAX_IPS; ++ii) // Up to _MAX_IPS allowed IPs
	{
		sprintf(ip, "IP %d", ii + 1);
		buffer[0] = 0;
		ExtConfig.GetString(++pos, ip, buffer, sizeof(buffer)-1);
		if (strlen(buffer) <= 0)
		{
			--pos;
			ExtConfig.Delete(ip);
			break;
		}
		m_arrIP[ii] = inet_addr(buffer);
		ExtServer->LogsOut(CmdOK, "FXPlugin ip", buffer);
	}
	//---- conver IP to number format
	m_ip = inet_addr(buffer);
	//---- get login
	ExtConfig.GetString(++pos, "Login", buffer, sizeof(buffer)-1, "4");
	m_login = atoi(buffer);
	// get order quantity
	ExtConfig.GetString(++pos, "Qty", buffer, sizeof(buffer)-1, "100");
	m_volume = atoi(buffer);
	//---- get symbol
	ExtConfig.GetString(++pos, "Symbol", buffer, sizeof(buffer)-1, "EURUSD");
	strcpy(m_symbol, buffer);
	//
	//ExamplePositions();

	_snprintf(buffer, 1023, "ref count = %d", CSingleLock::m_sCount);
	ExtServer->LogsOut(CmdOK, "FXPlugin ref count", buffer);
}

void CProcessor::Terminate(void)
{
	if (m_threadServer)
	{
		m_socketServer->m_finished = TRUE;
		ExtServer->LogsOut(CmdOK, "FXGlobal Extension", "Terminate");
		WaitForSingleObject(m_threadServer, 10000);
		CloseHandle(m_threadServer);
		m_threadServer = NULL;
		ExtServer->LogsOut(CmdOK, "FXGlobal Extension", "Terminate 2");
	}
}

SOrderInfo& CProcessor::TradeRecordToOrderInfo(TradeRecord& tradeRecord, SOrderInfo& orderInfo)
{
	orderInfo.Login = tradeRecord.login;
	SGetUserInfo ui;
	SGetUserInfoReq req;
	req.Login = orderInfo.Login;
	GetUserInfo(&req, &ui);
	if (ui.ReturnCode == E_Success)
	{
		COPY_STR(orderInfo.Group, ui.Group);
	}
	COPY_STR(orderInfo.Comment, tradeRecord.comment);
	COPY_STR(orderInfo.Symbol, tradeRecord.symbol);
	orderInfo.Side = (eOrderSide)tradeRecord.cmd;
	orderInfo.Volume = tradeRecord.volume;
	orderInfo.State = tradeRecord.state;
	if (tradeRecord.state == TS_OPEN_NORMAL || tradeRecord.state == TS_OPEN_REMAND || tradeRecord.state == TS_OPEN_RESTORED)
	{
		orderInfo.Price = tradeRecord.open_price;
		orderInfo.Time = tradeRecord.open_time;
		orderInfo.Ticket = tradeRecord.order;
	}
	else if (tradeRecord.state == TS_CLOSED_NORMAL || tradeRecord.state == TS_CLOSED_PART || tradeRecord.state == TS_CLOSED_BY)
	{
		orderInfo.Price = tradeRecord.close_price;
		orderInfo.Time = tradeRecord.close_time;
		if (tradeRecord.state == TS_CLOSED_PART)
		{
			orderInfo.Ticket = tradeRecord.order;
			char* ix = strchr(tradeRecord.comment, '#');
			if (ix != NULL)
			{
				++ix;
				orderInfo.NewTicket = atoi(ix);
			}
		}
		else
		{
			orderInfo.Ticket = tradeRecord.order;
		}
	}
	else // TS_DELETED
	{
	}
	return orderInfo;
}

struct STradesPerUser
{
	STradesPerUser()
	{
		Total = 0;
		Trades = NULL;
	}

	~STradesPerUser()
	{
		if (Trades != NULL)
			HEAP_FREE(Trades);
	}

	int Total;
	TradeRecord* Trades;
};

STradeAdd* CProcessor::GetAllTrades(SGetTrades& getTrades, time_t From, time_t To, eGetOrderType getOrderType, int& total)
{
	STradeAdd* ret = NULL;
	int total_users = 0;
	UserRecord* users = ExtServer->ClientsAllUsers(&total_users);
	if (total_users <= 0)
		return ret;
	total = 0;
	// Calculate total orders first
	STradesPerUser* arr_tr = new STradesPerUser[total_users];
	for (int ii = 0; ii < total_users; ++ii)
	{
		int total_per_user = 0;
		TradeRecord* tr = NULL;
		if (getOrderType == E_AllOrders)
		{
			tr = ExtServer->OrdersGet(From, To, &users[ii].login, 1, &total_per_user);
		}
		else if (getOrderType == E_OpenOrders)
		{
			UserInfo user;
			UserInfoGet(users[ii].login, &user);
			tr = ExtServer->OrdersGetOpen(&user, &total_per_user);
		}
		else if (getOrderType == E_CloseOrders)
		{
			tr = ExtServer->OrdersGetClosed(From, To, &users[ii].login, 1, &total_per_user);
		}
		arr_tr[ii].Total = total_per_user;
		arr_tr[ii].Trades = tr;
		total += total_per_user;
	}
	// Prepare result
	int hh = 0;
	ret = new STradeAdd[total];
	for (int ii = 0; ii < total_users; ++ii)
	{
		for (int jj = 0; jj < arr_tr[ii].Total; ++jj)
		{
			ret[hh].MessageType = E_GetTrades;
			ret[hh].ClientTime = getTrades.ClientTime;
			TradeRecordToOrderInfo(arr_tr[ii].Trades[jj], ret[hh].OrderInfo);
			++hh;
		}
	}

	delete[] arr_tr;
	return ret;
}

STradeAdd* CProcessor::GetTrades(SGetTrades& getTrades, int& total)
{
	STradeAdd* ret = NULL;
	if (getTrades.Group[0] == 0 || strlen(getTrades.Group) == 0) // By login number
		ret = GetTrades(getTrades, getTrades.From, getTrades.To, getTrades.login, getTrades.GetOrderType, total);
	else // By group name
		ret = GetTrades(getTrades, getTrades.Group, getTrades.From, getTrades.To, getTrades.GetOrderType, total);
	return ret;
}

STradeAdd* CProcessor::GetTrades(SGetTrades& getTrades, time_t From, time_t To, int login, eGetOrderType getOrderType, int& total)
{
	STradeAdd* ret = NULL;
	TradeRecord* tr = NULL;

	if (getOrderType == E_AllOrders)
	{
		tr = ExtServer->OrdersGet(From, To, &login, 1, &total);
	}
	else if (getOrderType == E_OpenOrders)
	{
		UserInfo user;
		UserInfoGet(login, &user);
		tr = ExtServer->OrdersGetOpen(&user, &total);
	}
	else if (getOrderType == E_CloseOrders)
	{
		tr = ExtServer->OrdersGetClosed(From, To, &login, 1, &total);
	}
	if (tr != NULL)
	{
		ret = new STradeAdd[total];
		for (int ii = 0; ii < total; ++ii)
		{
			ret[ii].MessageType = E_GetTrades;
			ret[ii].ClientTime = getTrades.ClientTime;
			TradeRecordToOrderInfo(tr[ii], ret[ii].OrderInfo);
		}
		HEAP_FREE(tr);
	}
	return ret;
}

struct SOpenTradesByGroup
{
	SOpenTradesByGroup()
	{
		Size = 0;
		TradeRecord = NULL;
	}

	void Clear()
	{
		if (TradeRecord != NULL)
		{
			HEAP_FREE(TradeRecord);
			Size = 0;
		}
	}

	SOpenTradesByGroup(const SOpenTradesByGroup& Obj)
	{
		Size = Obj.Size;
		TradeRecord = Obj.TradeRecord;
	}

	int Size;
	TradeRecord* TradeRecord;
};

STradeAdd* CProcessor::GetOpenTrades(SGetTrades& getTrades, const char* group, int& total)
{
	STradeAdd* ret = NULL;
	std::vector<SOpenTradesByGroup> vec;
	total = 0;
	int tmp_total = 0;
	int total_users = 0;
	UserRecord* ur = ExtServer->ClientsGroupsUsers(&total_users, group);
	for (int ii = 0; ii < total_users; ++ii)
	{
		UserInfo user;
		UserInfoGet(ur->login, &user);
		TradeRecord* tr = ExtServer->OrdersGetOpen(&user, &tmp_total);
		if (tmp_total > 0)
		{
			SOpenTradesByGroup elem;
			elem.Size = tmp_total;
			elem.TradeRecord = tr;
			vec.push_back(elem);
			total += tmp_total;
		}
	}
	if (total > 0)
	{
		ret = new STradeAdd[total];
		for (int ii = 0, hh = 0; ii < vec.size(); ++ii)
		{
			for (int jj = 0; jj < vec[ii].Size; ++jj, ++hh)
			{
				ret[hh].MessageType = E_GetTrades;
				ret[hh].ClientTime = getTrades.ClientTime;
				TradeRecordToOrderInfo(vec[ii].TradeRecord[jj], ret[hh].OrderInfo);
			}
			vec[ii].Clear();
		}
	}
	return ret;
}

STradeAdd* CProcessor::GetTrades(SGetTrades& getTrades, const char* group, time_t From, time_t To, eGetOrderType getOrderType, int& total)
{
	STradeAdd* ret = NULL;
	TradeRecord* tr = NULL;
	std::vector<SOpenTradesByGroup> vec;
	total = 0;
	int tmp_total = 0;
	int total_users = 0;
	UserRecord* ur = ExtServer->ClientsGroupsUsers(&total_users, group);
	for (int ii = 0; ii < total_users; ++ii)
	{
		UserInfo user;
		UserInfoGet(ur->login, &user);
		if (getOrderType == E_AllOrders)
		{
			tr = ExtServer->OrdersGet(From, To, &ur->login, 1, &tmp_total);
		}
		else if (getOrderType == E_OpenOrders)
		{
			tr = ExtServer->OrdersGetOpen(&user, &tmp_total);
		}
		else if (getOrderType == E_CloseOrders)
		{
			tr = ExtServer->OrdersGetClosed(From, To, &ur->login, 1, &tmp_total);
		}
		if (tmp_total > 0)
		{
			SOpenTradesByGroup elem;
			elem.Size = tmp_total;
			elem.TradeRecord = tr;
			vec.push_back(elem);
			total += tmp_total;
		}
	}
	if (total > 0)
	{
		ret = new STradeAdd[total];
		for (int ii = 0, hh = 0; ii < vec.size(); ++ii)
		{
			for (int jj = 0; jj < vec[ii].Size; ++jj, ++hh)
			{
				ret[hh].MessageType = E_GetTrades;
				ret[hh].ClientTime = getTrades.ClientTime;
				TradeRecordToOrderInfo(vec[ii].TradeRecord[jj], ret[hh].OrderInfo);
			}
			vec[ii].Clear();
		}
	}
	return ret;
}

SReturnCode CProcessor::CheckLogin(SLogin* Obj)
{
	SReturnCode ret;
	ret.ClientTime = Obj->ClientTime;
	ret.CalledMessageType = E_Login;
	char buffer[256];
	sprintf(buffer, "m_user=%s,password=%s,Obj->User=%s,Obj->Password=%s", m_user, m_password, Obj->User, Obj->Password);
	ExtServer->LogsOut(CmdOK, "FXPlugin ip", buffer);

	if (!(strcmp(Obj->User, m_user) && strcmp(Obj->Password, m_password)))
	{
		ret.ReturnCode = E_Success;
	}
	else
	{
		ret.ReturnCode = E_Failure;
	}
	return ret;
}

int CProcessor::CheckBasicInfo(const ULONG ip, char *buffer, const int size)
{
	int ret = 0;
	char       temp[256];
	CSingleLock lock(&m_sync);
	_snprintf(temp, 1023, "ref count = %d", CSingleLock::m_sCount);
	ExtServer->LogsOut(CmdOK, "FXPlugin ref count", temp);
	//--- parsing
	if (!IsIPExists(ip))
	{
		ret = _snprintf(buffer, size - 1, "ERROR\r\ninvalid IP\r\nend\r\n");
	}
	else if (GetStrParam(buffer, "USER=", temp, sizeof(temp)-1) == FALSE || strcmp(temp, m_user))
	{
		ExtServer->LogsOut(CmdOK, "FXPlugin", m_user);
		ret = _snprintf(buffer, size - 1, "ERROR\r\ninvalid USER / password\r\nend\r\n");
	}
	else if (GetStrParam(buffer, "PWD=", temp, sizeof(temp)-1) == FALSE || strcmp(temp, m_password))
	{
		ExtServer->LogsOut(CmdOK, "FXPlugin", m_password);
		ret = _snprintf(buffer, size - 1, "ERROR\r\ninvalid USER / password\r\nend\r\n");
	}
	return ret;
}


//+------------------------------------------------------------------+
//| Telnet Extension entry                                           |
//| (dont forget to send 'W' for telnet mode):                       |
//+------------------------------------------------------------------+
int CProcessor::SrvTelnet(const ULONG ip, char *buffer, const int size)
{
	int ret = 0;
	char temp[256];
	_snprintf(temp, size - 1, "MtSrvTelnet ip = %u, size = %d", ip, size);
	ExtServer->LogsOut(CmdOK, "FXPlugin", temp);
	
	//--- checking
	if (ExtServer == NULL || buffer == NULL || size < 1)
	{
		return(0);
	}
	
	CSingleLock lock(&m_sync);
	_snprintf(temp, 1023, "ref count = %d", CSingleLock::m_sCount);
	ExtServer->LogsOut(CmdOK, "FXPlugin ref count", temp);

	// CheckBasicInfo
	ret = CheckBasicInfo(ip, buffer, size);
	if (ret)
	{
		return ret;
	}
	// Check command
	if (memcmp(buffer, "FXLOGIN", strlen("FXLOGIN")) == 0)
	{
		return _snprintf(buffer, size - 1, "OK\r\nUser=%s\r\nend\r\n", m_user);
	}

	//if (CheckLogin(m_login, temp))
	//{
	//	if (memcmp(buffer, "FXLOGIN", strlen("FXLOGIN")) == 0)
	//	{
	//		m_sync.Unlock();
	//		return _snprintf(buffer, size - 1, "OK\r\nLOGIN=%d\r\nend\r\n", m_login);
	//	}
	//}
	//else
	//{
	//	m_sync.Unlock();
	//	return _snprintf(buffer, size - 1, "FAILURE\r\nLOGIN=%d\r\nend\r\n", m_login);
	//}

	////---- receive master password and check it
	//if (GetStrParam(buffer, "MASTER=", temp, sizeof(temp)-1) == FALSE || strcmp(temp, m_password) != 0)
	//{
	//	m_sync.Unlock();
	//	return _snprintf(buffer, size - 1, "ERROR\r\ninvalid master / password\r\nend\r\n");
	//}

	//if (memcmp(buffer, "NEWACCOUNT", 10) != 0)
	//{

	ExtServer->LogsOut(CmdOK, "FXPlugin", "End MtSrvTelnet");
	return 0;
}

////--- trade record state
//enum { TS_OPEN_NORMAL, TS_OPEN_REMAND, TS_OPEN_RESTORED, TS_CLOSED_NORMAL, TS_CLOSED_PART, TS_CLOSED_BY, TS_DELETED };

//--- trade commands
//enum { OP_BUY = 0, OP_SELL, OP_BUY_LIMIT, OP_SELL_LIMIT, OP_BUY_STOP, OP_SELL_STOP, OP_BALANCE, OP_CREDIT };

void CProcessor::SrvTradesAdd(TradeRecord *trade, const UserInfo *user, const ConSymbol *symbol)
{
	Out(CmdTrade, "FXPlugin::SrvTradesAdd", "%d,%d,%d,%s,%s,%s", trade->order, trade->state, user->login, user->name, symbol->symbol, trade->comment);
	STradeAdd obj;
	TradeRecordToOrderInfo(*trade, obj.OrderInfo);
	m_socketServer->SendAll(&obj);
}

void CProcessor::SrvTradeRequestApply(RequestInfo *request, const int isdemo)
{
	Out(CmdTrade, "FXPlugin::SrvTradeRequestApply", "%d,%d,%d", request->id, request->login, isdemo);
}

void CProcessor::SrvTradesAddExt(TradeRecord *trade, const UserInfo *user, const ConSymbol *symbol, const int mode)
{
	Out(CmdTrade, "FXPlugin::SrvTradesAddExt", "%d,%d,%d,%s,%s,%s,%d", trade->order, trade->state, user->login, user->name, symbol->symbol, trade->comment, mode);
}

//--- trade update modes
//enum { UPDATE_NORMAL, UPDATE_ACTIVATE, UPDATE_CLOSE, UPDATE_DELETE };
void CProcessor::SrvTradesUpdate(TradeRecord *trade, UserInfo *user, const int mode)
{
	Out(CmdTrade, "FXPlugin::SrvTradesUpdate", "%d,%d,%d,%s,%s,%d", trade->order, trade->state, user->login, user->name, trade->comment, mode);
	STradeAdd obj;
	TradeRecordToOrderInfo(*trade, obj.OrderInfo);
	m_socketServer->SendAll(&obj);
}

//+------------------------------------------------------------------+
//| Example: open and close positions                                |
//+------------------------------------------------------------------+
void CProcessor::ExamplePositions(void)
{
	SOrderReturnCode obj_order;
	UserRecord user = { 0 };
	double     prices[2] = { 0 };
	int        order = 0;
	//--- checks
	if (m_login <= 0 || m_symbol[0] == '\0') return;
	//--- super check: plugin doesn't work with real accounts!!!
	if (ExtServer->ClientsUserInfo(m_login, &user) == FALSE || strcmp(user.group, "demo") == NULL)
	{
		Out(CmdErr, "FXPlugin", "ExamplePositions: %d in not demo group", m_login);
		return;
	}
	//--- get current prices
	if (ExtServer->HistoryPrices(m_symbol, prices, NULL, NULL) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "ExamplePositions: no prices for %s", m_symbol);
		return;
	}
	//--- open position with OrdersOpen
	obj_order = OrdersOpen(m_login, OP_BUY, m_symbol, prices[1], m_volume, "");
	if (obj_order.ReturnCode == E_Success)
	{
		//--- close position with OrdersClose
		obj_order = OrdersClose(obj_order.OrderID, m_volume, prices[0], "");
		if (obj_order.ReturnCode == E_Failure)
			Out(CmdErr, "FXPlugin", "ExamplePositions: OrdersClose failed");
	}
	else Out(CmdErr, "FXPlugin", "ExamplePositions: OrdersOpen failed");
	//--- open position with OrdersAdd
	if ((order = OrdersAddOpen(m_login, OP_BUY, m_symbol, prices[1], m_volume)) != 0)
	{
		obj_order = OrdersUpdateClose(order, m_volume, prices[0], "");
		//--- close position with OrdersUpdate
		if (obj_order.ReturnCode == E_Failure)
			Out(CmdErr, "FXPlugin", "ExamplePositions: OrdersUpdateClose failed");
	}
	else Out(CmdErr, "FXPlugin", "ExamplePositions: OrdersAddOpen failed");
}
//+------------------------------------------------------------------+
//| Example: open, activate and delete pending order                 |
//+------------------------------------------------------------------+
void CProcessor::ExamplePendings(void)
{
	SOrderReturnCode obj_order;
	UserRecord user = { 0 };
	double     prices[2] = { 0 }, open_price = 0;
	ConSymbol  symcfg = { 0 };
	int        order = { 0 };
	//--- checks
	if (m_login <= 0 || m_symbol[0] == '\0') return;
	//--- super check: plugin doesn't work with real accounts!!!
	if (ExtServer->ClientsUserInfo(m_login, &user) == FALSE || strcmp(user.group, "demo") == NULL)
	{
		Out(CmdErr, "FXPlugin", "ExamplePositions: %d in not demo group", m_login);
		return;
	}
	//--- get current prices
	if (ExtServer->HistoryPrices(m_symbol, prices, NULL, NULL) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "ExamplePendings: no prices for %s", m_symbol);
		return;
	}
	//--- get symbol config
	if (ExtServer->SymbolsGet(m_symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "ExamplePendings: SymbolsGet failed [%s]", m_symbol);
		return;
	}
	//--- calc opend price for pending (for passing Stops cheks)
	open_price = NormalizeDouble(prices[1] - max(1, symcfg.stops_level)*symcfg.point, symcfg.digits);
	//--- OrdersAdd/OrdersUpdate
	if ((order = OrdersAddOpenPending(m_login, OP_BUY_LIMIT, m_symbol, open_price, m_volume, 0)) != 0)
	{
		if (OrdersUpdateActivate(order, open_price) == FALSE)
			Out(CmdErr, "FXPlugin", "ExamplePendings: OrdersUpdateActivate failed");
		else
		{
			obj_order = OrdersUpdateClose(order, m_volume, prices[0], "");
			if (obj_order.ReturnCode == E_Failure)
				Out(CmdErr, "FXPlugin", "ExamplePendings: OrdersUpdateClose failed");
		}
	}
	else Out(CmdErr, "FXPlugin", "ExamplePendings: OrdersAddOpenPending failed");
}

//+------------------------------------------------------------------+
//| Output to server log                                             |
//+------------------------------------------------------------------+
void CProcessor::Out(const int code, LPCSTR ip, LPCSTR msg, ...) const
{
	char buffer[1024] = { 0 };
	//---- checks
	if (msg == NULL || ExtServer == NULL) return;
	//---- formating string
	va_list ptr;
	va_start(ptr, msg);
	_vsnprintf_s(buffer, sizeof(buffer)-1, msg, ptr);
	va_end(ptr);
	//---- output
	ExtServer->LogsOut(code, ip, buffer);
}

//+------------------------------------------------------------------+
//| Prepare UserInfo for login                                       |
//+------------------------------------------------------------------+
int CProcessor::UserInfoGet(const int login, UserInfo *info)
{
	UserRecord user = { 0 };
	//---- che?ks
	if (login<1 || info == NULL || ExtServer == NULL) return(FALSE);
	//---- clear info
	ZeroMemory(info, sizeof(UserInfo));
	//---- get user record
	if (ExtServer->ClientsUserInfo(login, &user) == FALSE) return(FALSE);
	//---- fill login
	info->login = user.login;
	//---- fill permissions
	info->enable = user.enable;
	info->enable_read_only = user.enable_read_only;
	//---- fill trade data
	info->leverage = user.leverage;
	info->agent_account = user.agent_account;
	info->credit = user.credit;
	info->balance = user.balance;
	info->prevbalance = user.prevbalance;
	//---- fill group
	COPY_STR(info->group, user.group);
	//---- ok
	return(TRUE);
}

void CProcessor::GetUserInfo(SGetUserInfoReq* Obj, SGetUserInfo *info)
{
	info->CalledMessageType = E_GetUserInfo;
	info->ClientTime = Obj->ClientTime;
	info->ReturnCode = E_Failure;
	info->Login = Obj->Login;
	UserRecord user = { 0 };
	//---- checks
	if (info->Login < 1 || info == NULL || ExtServer == NULL)
	{
		sprintf(info->ErrorDescription, "Invalid input");
	}
	//---- get user record
	if (ExtServer->ClientsUserInfo(info->Login, &user) == FALSE)
	{
		sprintf(info->ErrorDescription, "Call to ClientsUserInfo failed");
		return;
	}
	info->Login = user.login;
	//---- fill trade data
	info->Leverage = user.leverage;
	info->Credit = user.credit;
	info->Balance = user.balance;
	//---- fill group
	COPY_STR(info->Group, user.group);
	//---- fill name
	COPY_STR(info->Name, user.name);
	//---- ok
	info->ReturnCode = E_Success;
}

SReturnCode CProcessor::CreateUser(SCreateUser& Obj)
{
	SReturnCode ret;
	ret.CalledMessageType = E_CreateUser;
	ret.ClientTime = Obj.ClientTime;
	ret.ReturnCode = E_Failure;
	UserRecord ur;
	ur.login = Obj.Login;
	ur.balance = Obj.Deposit;
	COPY_STR(ur.name, Obj.Name);
	COPY_STR(ur.group, Obj.Group);
	COPY_STR(ur.password, Obj.Password);

	if (ExtServer->ClientsAddUser(&ur) != FALSE)
	{
		ret.ReturnCode = E_Success;
	}
	else
	{
		sprintf(ret.ErrorDescription, "Call to ClientsAddUser failed");
	}
	return ret;
}

//+------------------------------------------------------------------+
//| Open BUY or SELL position using OrdersOpen                       |
//+------------------------------------------------------------------+
SOrderReturnCode CProcessor::OrdersOpen(SOpenOrder& Order)
{
	SOrderReturnCode ret;
	ret.ClientTime = Order.ClientTime;
	ret.ReturnCode = E_Success;
	ret.CalledMessageType = E_OpenOrder;

	int login = Order.OrderInfo.Login;
	int cmd = Order.OrderInfo.Side;
	int open_price = Order.OrderInfo.Price;
	int volume = Order.OrderInfo.Volume;
	LPCTSTR symbol = Order.OrderInfo.Symbol;
	LPCTSTR comment = Order.OrderInfo.Comment;

	UserInfo       info = { 0 };
	ConSymbol      symcfg = { 0 };
	ConGroup       grpcfg = { 0 };
	TradeTransInfo trans = { 0 };
	int            order = 0;
	//--- checks
	if (login <= 0 || cmd<OP_BUY || cmd>OP_SELL || symbol == NULL || open_price <= 0 || volume <= 0 || ExtServer == NULL)
	{
		sprintf(ret.ErrorDescription, "OrdersOpen: invalid input parameters");
		ret.ReturnCode = E_Failure;
		//return(0);
	}
	//--- get user info
	else if (UserInfoGet(login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersOpen: UserInfoGet failed");
		sprintf(ret.ErrorDescription, "OrdersOpen: UserInfoGet failed for login (%d)", login);
		ret.ReturnCode = E_Failure;
		//return(FALSE); // error
	}
	// For testing *******************
	if (login == 4 && info.balance <= 0 && !strcmp(info.group, "demoforex"))
	{
		ConGroup group;
		if (ExtServer->GroupsGet(info.group, &group) == TRUE)
		{
			ExtServer->ClientsChangeBalance(login, &group, 100000, "OferZ test");
		}
	}

	//--- get group config
	else if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersOpen: GroupsGet failed [%s]", info.group);
		sprintf(ret.ErrorDescription, "OrdersOpen: GroupsGet failed [%s]", info.group);
		ret.ReturnCode = E_Failure;
		//return(0); // error
	}
	//--- get symbol config
	else if (ExtServer->SymbolsGet(symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersOpen: SymbolsGet failed [%s]", symbol);
		sprintf(ret.ErrorDescription, "OrdersOpen: SymbolsGet failed [%s]", symbol);
		ret.ReturnCode = E_Failure;
		//return(0); // error
	}
	else
	{
		//--- prepare transaction
		trans.cmd = cmd;
		trans.volume = volume;
		trans.price = open_price;
		COPY_STR(trans.symbol, symbol);
		COPY_STR(trans.comment, comment);
		//--- fill SL,TP, comment
		//--- check long only
		if (symcfg.long_only != FALSE && cmd == OP_SELL)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: long only allowed");
			sprintf(ret.ErrorDescription, "OrdersOpen: long only allowed");
			ret.ReturnCode = E_Failure;
			//return(0); // long only
		}
		//--- check close only
		else if (symcfg.trade == TRADE_CLOSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: close only allowed");
			sprintf(ret.ErrorDescription, "OrdersOpen: close only allowed");
			ret.ReturnCode = E_Failure;
			//return(0); // close only
		}
		//--- check tick size
		else if (ExtServer->TradesCheckTickSize(open_price, &symcfg) == FALSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: invalid price");
			sprintf(ret.ErrorDescription, "OrdersOpen: invalid price");
			ret.ReturnCode = E_Failure;
			//return(0); // invalid price
		}
		//--- check secutiry
		else if (ExtServer->TradesCheckSecurity(&symcfg, &grpcfg) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: trade disabled or market closed");
			sprintf(ret.ErrorDescription, "OrdersOpen: trade disabled or market closed");
			ret.ReturnCode = E_Failure;
			//return(0); // trade disabled, market closed, or no prices for long time
		}
		//--- check volume
		else if (ExtServer->TradesCheckVolume(&trans, &symcfg, &grpcfg, TRUE) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: invalid volume");
			sprintf(ret.ErrorDescription, "OrdersOpen: invalid volume");
			ret.ReturnCode = E_Failure;
			//return(0); // invalid volume
		}
		//--- check stops
		else if (ExtServer->TradesCheckStops(&trans, &symcfg, &grpcfg, NULL) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: invalid stops");
			sprintf(ret.ErrorDescription, "OrdersOpen: invalid stops");
			ret.ReturnCode = E_Failure;
			//return(0); // invalid stops
		}
		//--- open order with margin check
		else if ((order = ExtServer->OrdersOpen(&trans, &info)) == 0)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: OrdersOpen failed");
			sprintf(ret.ErrorDescription, "OrdersOpen: OrdersOpen failed due to margin");
			ret.ReturnCode = E_Failure;
			//return(0); // error
		}
		else
		{
			ret.OrderID = order;
		}
	}
	//--- postion opened: return order
	return ret;
	//return(order);
}

SOrderReturnCode CProcessor::OrdersOpen(const int login, const int cmd, LPCTSTR symbol, const double open_price, const int volume, LPCTSTR comment, CSocketClient* socket)
{
	SOrderReturnCode ret;
	ret.ReturnCode = E_Success;
	ret.CalledMessageType = E_OpenOrder;

	UserInfo       info = { 0 };
	ConSymbol      symcfg = { 0 };
	ConGroup       grpcfg = { 0 };
	TradeTransInfo trans = { 0 };
	int            order = 0;
	//--- checks
	if (login <= 0 || cmd<OP_BUY || cmd>OP_SELL || symbol == NULL || open_price <= 0 || volume <= 0 || ExtServer == NULL)
	{
		sprintf(ret.ErrorDescription, "OrdersOpen: invalid input parameters");
		ret.ReturnCode = E_Failure;
		//return(0);
	}
	//--- get user info
	else if (UserInfoGet(login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersOpen: UserInfoGet failed");
		sprintf(ret.ErrorDescription, "OrdersOpen: UserInfoGet failed for login (%d)", login);
		ret.ReturnCode = E_Failure;
		//return(FALSE); // error
	}
	// For testing *******************
	if (login == 4 && info.balance <= 0 && !strcmp(info.group, "demoforex"))
	{
		ConGroup group;
		if (ExtServer->GroupsGet(info.group, &group) == TRUE)
		{
			ExtServer->ClientsChangeBalance(login, &group, 100000, "OferZ test");
		}
	}

	//--- get group config
	else if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersOpen: GroupsGet failed [%s]", info.group);
		sprintf(ret.ErrorDescription, "OrdersOpen: GroupsGet failed [%s]", info.group);
		ret.ReturnCode = E_Failure;
		//return(0); // error
	}
	//--- get symbol config
	else if (ExtServer->SymbolsGet(symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersOpen: SymbolsGet failed [%s]", symbol);
		sprintf(ret.ErrorDescription, "OrdersOpen: SymbolsGet failed [%s]", symbol);
		ret.ReturnCode = E_Failure;
		//return(0); // error
	}
	else
	{
		//--- prepare transaction
		trans.cmd = cmd;
		trans.volume = volume;
		trans.price = open_price;
		COPY_STR(trans.symbol, symbol);
		COPY_STR(trans.comment, comment);
		//--- fill SL,TP, comment
		//--- check long only
		if (symcfg.long_only != FALSE && cmd == OP_SELL)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: long only allowed");
			sprintf(ret.ErrorDescription, "OrdersOpen: long only allowed");
			ret.ReturnCode = E_Failure;
			//return(0); // long only
		}
		//--- check close only
		else if (symcfg.trade == TRADE_CLOSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: close only allowed");
			sprintf(ret.ErrorDescription, "OrdersOpen: close only allowed");
			ret.ReturnCode = E_Failure;
			//return(0); // close only
		}
		//--- check tick size
		else if (ExtServer->TradesCheckTickSize(open_price, &symcfg) == FALSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: invalid price");
			sprintf(ret.ErrorDescription, "OrdersOpen: invalid price");
			ret.ReturnCode = E_Failure;
			//return(0); // invalid price
		}
		//--- check secutiry
		else if (ExtServer->TradesCheckSecurity(&symcfg, &grpcfg) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: trade disabled or market closed");
			sprintf(ret.ErrorDescription, "OrdersOpen: trade disabled or market closed");
			ret.ReturnCode = E_Failure;
			//return(0); // trade disabled, market closed, or no prices for long time
		}
		//--- check volume
		else if (ExtServer->TradesCheckVolume(&trans, &symcfg, &grpcfg, TRUE) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: invalid volume");
			sprintf(ret.ErrorDescription, "OrdersOpen: invalid volume");
			ret.ReturnCode = E_Failure;
			//return(0); // invalid volume
		}
		//--- check stops
		else if (ExtServer->TradesCheckStops(&trans, &symcfg, &grpcfg, NULL) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: invalid stops");
			sprintf(ret.ErrorDescription, "OrdersOpen: invalid stops");
			ret.ReturnCode = E_Failure;
			//return(0); // invalid stops
		}
		//--- open order with margin check
		else if ((order = ExtServer->OrdersOpen(&trans, &info)) == 0)
		{
			Out(CmdErr, "FXPlugin", "OrdersOpen: OrdersOpen failed");
			sprintf(ret.ErrorDescription, "OrdersOpen: OrdersOpen failed due to margin");
			ret.ReturnCode = E_Failure;
			//return(0); // error
		}
		else
		{
			ret.OrderID = order;
		}
	}
	//--- postion opened: return order
	return ret;
	//return(order);
}

//+------------------------------------------------------------------+
//| Open BUY or SELL position using OrdersAdd                        |
//+------------------------------------------------------------------+
int CProcessor::OrdersAddOpen(const int login, const int cmd, LPCTSTR symbol, const double open_price, const int volume)
{
	UserInfo       info = { 0 };
	ConGroup       grpcfg = { 0 };
	ConSymbol      symcfg = { 0 };
	TradeTransInfo trans = { 0 };
	TradeRecord    trade = { 0 };
	double         curprices[2] = { 0 }, profit = 0, margin = 0, freemargin = 0, prevmargin = 0;
	int            order = 0;
	//--- checks
	if (login <= 0 || cmd<OP_BUY || cmd>OP_SELL || symbol == NULL || open_price <= 0 || volume <= 0 || ExtServer == NULL)
		return(0);
	//--- get user info
	if (UserInfoGet(login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: UserInfoGet failed");
		return(0); // error
	}
	//--- get group config
	if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: GroupsGet failed [%s]", info.group);
		return(0); // error
	}
	//--- get symbol config
	if (ExtServer->SymbolsGet(symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: SymbolsGet failed [%s]", m_symbol);
		return(0); // error
	}
	//--- check long only
	if (symcfg.long_only != FALSE && cmd == OP_SELL)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: long only allowed");
		return(0); // long only
	}
	//--- check close only
	if (symcfg.trade == TRADE_CLOSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: close only allowed");
		return(0); // close only
	}
	//--- prepare transaction for checks
	trans.cmd = cmd;
	trans.volume = volume;
	trans.price = open_price;
	COPY_STR(trans.symbol, symbol);
	//--- check tick size
	if (ExtServer->TradesCheckTickSize(open_price, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: invalid price");
		return(0); // invalid price
	}
	//--- check secutiry
	if (ExtServer->TradesCheckSecurity(&symcfg, &grpcfg) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: trade disabled or market closed");
		return(0); // trade disabled, market closed, or no prices for long time
	}
	//--- check volume
	if (ExtServer->TradesCheckVolume(&trans, &symcfg, &grpcfg, TRUE) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: invalid volume");
		return(0); // invalid volume
	}
	//--- check stops
	if (ExtServer->TradesCheckStops(&trans, &symcfg, &grpcfg, NULL) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: invalid stops");
		return(0); // invalid stops
	}
	//--- check margin
	margin = ExtServer->TradesMarginCheck(&info, &trans, &profit, &freemargin, &prevmargin);
	if ((freemargin + grpcfg.credit)<0 && (symcfg.margin_hedged_strong != FALSE || prevmargin <= margin))
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: not enough margin");
		return(0); // no enough margin
	}
	//--- get current prices
	if (ExtServer->HistoryPricesGroup(symbol, &grpcfg, curprices) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: no prices for group");
		return(0); // error
	}
	//--- prepare new trade state of order
	trade.login = login;
	trade.cmd = cmd;
	trade.open_price = open_price;
	trade.volume = volume;
	trade.close_price = (cmd == OP_BUY ? curprices[0] : curprices[1]);
	trade.open_time = ExtServer->TradeTime();
	//trade.spread = symcfg.spread;
	trade.digits = symcfg.digits;
	COPY_STR(trade.symbol, symbol);
	//--- fill SL,TP, comment
	//--- add order 
	if ((order = ExtServer->OrdersAdd(&trade, &info, &symcfg)) == 0)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpen: OrdersAdd failed");
		return(0); // error
	}
	//--- postion opened: return order
	return(order);
}
//+------------------------------------------------------------------+
//| Close BUY or SELL position using OrdersClose                     |
//+------------------------------------------------------------------+
SOrderReturnCode CProcessor::OrdersClose(SCloseOrder& Order)
{
	SOrderReturnCode ret;
	ret.ClientTime = Order.ClientTime;
	ret.ReturnCode = E_Success;
	ret.CalledMessageType = E_CloseOrder;
	ret.OrderID = Order.OrderInfo.Ticket;
	int _order = 0;

	int order = Order.OrderInfo.Ticket;
	int close_price = Order.OrderInfo.Price;
	int volume = Order.OrderInfo.Volume;
	LPCTSTR comment = Order.OrderInfo.Comment;

	UserInfo       info = { 0 };
	ConGroup       grpcfg = { 0 };
	ConSymbol      symcfg = { 0 };
	TradeTransInfo trans = { 0 };
	TradeRecord    oldtrade = { 0 };
	//--- checks
	if (order <= 0 || volume <= 0 || close_price <= 0 || ExtServer == NULL)
	{
		sprintf(ret.ErrorDescription, "OrdersClose: invalid input parameters");
		ret.ReturnCode = E_Failure;
	}
	//--- get order
	else if (ExtServer->OrdersGet(order, &oldtrade) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: OrdersGet failed");
		sprintf(ret.ErrorDescription, "OrdersClose: OrdersGet failed");
		ret.ReturnCode = E_Failure;
	}
	else if (oldtrade.cmd == OP_BALANCE || oldtrade.cmd == OP_CREDIT)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: Trade Command is OP_BALANCE or OP_CREDIT");
		sprintf(ret.ErrorDescription, "OrdersClose: Trade Command is OP_BALANCE or OP_CREDIT");
		return ret; // SUCCESS
	}
	//--- get user info
	else if (UserInfoGet(oldtrade.login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: UserInfoGet failed");
		sprintf(ret.ErrorDescription, "OrdersClose: UserInfoGet failed for login %d", oldtrade.login);
		ret.ReturnCode = E_Failure;
	}
	//--- get group config
	else if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: GroupsGet failed [%s]", info.group);
		sprintf(ret.ErrorDescription, "OrdersClose: GroupsGet failed [%s]", info.group);
		ret.ReturnCode = E_Failure;
	}
	//--- get symbol config
	else if (ExtServer->SymbolsGet(oldtrade.symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: SymbolsGet failed [%s]", oldtrade.symbol);
		sprintf(ret.ErrorDescription, "OrdersClose: SymbolsGet failed [%s]", oldtrade.symbol);
		ret.ReturnCode = E_Failure;
	}
	else
	{
		//--- prepare transaction
		trans.order = order;
		trans.volume = volume;
		trans.price = close_price;
		COPY_STR(trans.comment, comment);
		//--- check tick size
		if (ExtServer->TradesCheckTickSize(close_price, &symcfg) == FALSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersClose: invalid price");
			sprintf(ret.ErrorDescription, "OrdersClose: invalid price");
			ret.ReturnCode = E_Failure;
		}
		//--- check secutiry
		else if (ExtServer->TradesCheckSecurity(&symcfg, &grpcfg) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: trade disabled or market closed");
			sprintf(ret.ErrorDescription, "OrdersClose: trade disabled or market closed");
			ret.ReturnCode = E_Failure;
		}
		//--- check volume
		else if (ExtServer->TradesCheckVolume(&trans, &symcfg, &grpcfg, TRUE) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: invalid volume");
			sprintf(ret.ErrorDescription, "OrdersClose: invalid volume");
			ret.ReturnCode = E_Failure;
		}
		//--- check stops
		else if (ExtServer->TradesCheckFreezed(&symcfg, &grpcfg, &oldtrade) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: position freezed");
			sprintf(ret.ErrorDescription, "OrdersClose: position freezed");
			ret.ReturnCode = E_Failure;
		}
		//--- close position
		else if ((_order = ExtServer->OrdersClose(&trans, &info)) == FALSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersClose: OrdersClose failed");
			sprintf(ret.ErrorDescription, "OrdersClose: OrdersClose failed");
			ret.ReturnCode = E_Failure;
		}
		else
		{
			if (ExtServer->OrdersGet(order, &oldtrade))
			{
				char* ix = strchr(oldtrade.comment, '#');
				if (ix != NULL)
				{
					++ix;
					ret.OrderID = atoi(ix);
				}
			}
		}
	}
	//--- position closed
	ret.IsPartial = oldtrade.state == TS_CLOSED_PART;
	return ret;
}

SOrderReturnCode CProcessor::OrdersClose(const int order, const int volume, const double close_price, LPCTSTR comment, CSocketClient* socket)
{
	SOrderReturnCode ret;
	ret.ReturnCode = E_Success;
	ret.CalledMessageType = E_CloseOrder;
	ret.OrderID = order;
	int _order = 0; 

	UserInfo       info = { 0 };
	ConGroup       grpcfg = { 0 };
	ConSymbol      symcfg = { 0 };
	TradeTransInfo trans = { 0 };
	TradeRecord    oldtrade = { 0 };
	//--- checks
	if (order <= 0 || volume <= 0 || close_price <= 0 || ExtServer == NULL)
	{
		sprintf(ret.ErrorDescription, "OrdersClose: invalid input parameters");
		ret.ReturnCode = E_Failure;
	}
	//--- get order
	else if (ExtServer->OrdersGet(order, &oldtrade) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: OrdersGet failed");
		sprintf(ret.ErrorDescription, "OrdersClose: OrdersGet failed");
		ret.ReturnCode = E_Failure;
	}
	//--- get user info
	else if (UserInfoGet(oldtrade.login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: UserInfoGet failed");
		sprintf(ret.ErrorDescription, "OrdersClose: UserInfoGet failed for login %d", oldtrade.login);
		ret.ReturnCode = E_Failure;
	}
	//--- get group config
	else if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: GroupsGet failed [%s]", info.group);
		sprintf(ret.ErrorDescription, "OrdersClose: GroupsGet failed [%s]", info.group);
		ret.ReturnCode = E_Failure;
	}
	//--- get symbol config
	else if (ExtServer->SymbolsGet(oldtrade.symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersClose: SymbolsGet failed [%s]", oldtrade.symbol);
		sprintf(ret.ErrorDescription, "OrdersClose: SymbolsGet failed [%s]", oldtrade.symbol);
		ret.ReturnCode = E_Failure;
	}
	else
	{
		//--- prepare transaction
		trans.order = order;
		trans.volume = volume;
		trans.price = close_price;
		COPY_STR(trans.comment, comment);
		//--- check tick size
		if (ExtServer->TradesCheckTickSize(close_price, &symcfg) == FALSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersClose: invalid price");
			sprintf(ret.ErrorDescription, "OrdersClose: invalid price");
			ret.ReturnCode = E_Failure;
		}
		//--- check secutiry
		else if (ExtServer->TradesCheckSecurity(&symcfg, &grpcfg) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: trade disabled or market closed");
			sprintf(ret.ErrorDescription, "OrdersClose: trade disabled or market closed");
			ret.ReturnCode = E_Failure;
		}
		//--- check volume
		else if (ExtServer->TradesCheckVolume(&trans, &symcfg, &grpcfg, TRUE) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: invalid volume");
			sprintf(ret.ErrorDescription, "OrdersClose: invalid volume");
			ret.ReturnCode = E_Failure;
		}
		//--- check stops
		else if (ExtServer->TradesCheckFreezed(&symcfg, &grpcfg, &oldtrade) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: position freezed");
			sprintf(ret.ErrorDescription, "OrdersClose: position freezed");
			ret.ReturnCode = E_Failure;
		}
		//--- close position
		else if ((_order = ExtServer->OrdersClose(&trans, &info)) == FALSE)
		{
			Out(CmdErr, "FXPlugin", "OrdersClose: OrdersClose failed");
			sprintf(ret.ErrorDescription, "OrdersClose: OrdersClose failed");
			ret.ReturnCode = E_Failure;
		}
		else
		{
			if (ExtServer->OrdersGet(order, &oldtrade))
			{
				char* ix = strchr(oldtrade.comment, '#');
				if (ix != NULL)
				{
					++ix;
					ret.OrderID = atoi(ix);
				}
			}
		}
	}
	//--- position closed
	ret.IsPartial = oldtrade.state == TS_CLOSED_PART;
	return ret;
}

//+------------------------------------------------------------------+
//| Close position using OrdersUpdate                                |
//+------------------------------------------------------------------+
SOrderReturnCode CProcessor::OrdersUpdateClose(const int order, const int volume, const double close_price, LPCTSTR comment, CSocketClient* socket)
{
	SOrderReturnCode ret;
	ret.ReturnCode = E_Success;
	ret.CalledMessageType = E_CloseOrder;
	ret.OrderID = order;
	int _order = 0;
	UserInfo       info = { 0 };
	ConSymbol      symcfg = { 0 };
	ConGroup       grpcfg = { 0 };
	TradeTransInfo trans = { 0 };
	TradeRecord    oldtrade = { 0 }, trade = { 0 };
	//--- checks
	if (order <= 0 || volume <= 0 || close_price <= 0 || ExtServer == NULL)
	{
		sprintf(ret.ErrorDescription, "OrdersClose: invalid input parameters");
		ret.ReturnCode = E_Failure;
	}
	//--- get order
	else if (ExtServer->OrdersGet(order, &oldtrade) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: OrdersGet failed");
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: OrdersGet failed");
		ret.ReturnCode = E_Failure;
	}
	//--- check state
	else if (oldtrade.cmd<OP_BUY || oldtrade.cmd>OP_SELL)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: invalid position");
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: invalid position");
		ret.ReturnCode = E_Failure;
	}
	//--- check close time
	else if (oldtrade.close_time != 0)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: position already closed");
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: position already closed");
		ret.ReturnCode = E_Failure;
	}
	//--- get user info
	else if (UserInfoGet(oldtrade.login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: UserInfoGet failed");
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: UserInfoGet failed");
		ret.ReturnCode = E_Failure;
	}
	//--- get group config
	else if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: GroupsGet failed [%s]", info.group);
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: GroupsGet failed[%s]", info.group);
		ret.ReturnCode = E_Failure;
	}
	//--- get symbol config
	else if (ExtServer->SymbolsGet(oldtrade.symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: SymbolsGet failed [%s]", oldtrade.symbol);
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: SymbolsGet failed [%s]", oldtrade.symbol);
		ret.ReturnCode = E_Failure;
	}
	//--- check tick size
	else if (ExtServer->TradesCheckTickSize(close_price, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: invalid price");
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: invalid price");
		ret.ReturnCode = E_Failure;
	}
	//--- check secutiry
	else if (ExtServer->TradesCheckSecurity(&symcfg, &grpcfg) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: trade disabled or market closed");
		sprintf(ret.ErrorDescription, "OrdersUpdateClose: trade disabled or market closed");
		ret.ReturnCode = E_Failure;
	}
	else
	{
		//--- prepare transaction for checks
		trans.volume = volume;
		//--- check volume
		if (ExtServer->TradesCheckVolume(&trans, &symcfg, &grpcfg, TRUE) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: invalid volume");
			sprintf(ret.ErrorDescription, "OrdersUpdateClose: invalid volume");
			ret.ReturnCode = E_Failure;
		}
		//--- check stops
		else if (ExtServer->TradesCheckFreezed(&symcfg, &grpcfg, &oldtrade) != RET_OK)
		{
			Out(CmdErr, "FXPlugin", "OrdersUpdateClose: invalid stops");
			sprintf(ret.ErrorDescription, "OrdersUpdateClose: invalid stops");
			ret.ReturnCode = E_Failure;
		}
		else
		{
			//--- prepare new trade state of order
			memcpy(&trade, &oldtrade, sizeof(TradeRecord));
			trade.close_time = ExtServer->TradeTime();
			trade.close_price = close_price;
			trade.volume = volume;
			//--- fill comment
			//--- calc profit
			ExtServer->TradesCalcProfit(info.group, &trade);
			//--- calc conv rates
			trade.conv_rates[1] = ExtServer->TradesCalcConvertation(info.group, FALSE, trade.close_price, &symcfg);
			//--- calc agent commission
			ExtServer->TradesCommissionAgent(&trade, &symcfg, &info);
			//--- close position
			if (ExtServer->OrdersUpdate(&trade, &info, UPDATE_CLOSE) == FALSE)
			{
				Out(CmdErr, "FXPlugin", "OrdersUpdateClose: OrdersUpdate failed");
				sprintf(ret.ErrorDescription, "OrdersUpdateClose: OrdersUpdate failed");
				ret.ReturnCode = E_Failure;
			}
			else
			{
				ret.OrderID = trade.order;
			}
		}
	}
	ret.IsPartial = oldtrade.volume > volume;
	//--- position closed
	return ret;
}

//+------------------------------------------------------------------+
//| Open pending order using OrdersAdd                               |
//+------------------------------------------------------------------+
int CProcessor::OrdersAddOpenPending(const int login, const int cmd, LPCTSTR symbol, const double open_price, const int volume, const time_t expiration)
{
	UserInfo       info = { 0 };
	ConGroup       grpcfg = { 0 };
	ConSymbol      symcfg = { 0 };
	TradeTransInfo trans = { 0 };
	TradeRecord    trade = { 0 };
	double         curprices[2] = { 0 }; // bid/ask
	int            order = 0;
	//--- checks
	if (login <= 0 || cmd<OP_BUY_LIMIT || cmd>OP_SELL_STOP || symbol == NULL ||
		open_price <= 0 || volume <= 0 || ExtServer == NULL) return(0);
	//--- get user info
	if (UserInfoGet(login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: UserInfoGet failed");
		return(0); // error
	}
	//--- get group config
	if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: GroupsGet failed [%s]", info.group);
		return(0); // error
	}
	//--- get symbol config
	if (ExtServer->SymbolsGet(symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: SymbolsGet failed [%s]", symbol);
		return(0); // error
	}
	//--- check long only
	if (symcfg.long_only != FALSE && cmd != OP_BUY_LIMIT && cmd != OP_SELL_STOP)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: long only allowed");
		return(0); // long only
	}
	//--- check close only
	if (symcfg.trade == TRADE_CLOSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: close only allowed");
		return(0); // close only
	}
	//--- prepare transaction for checks
	trans.cmd = cmd;
	trans.volume = volume;
	trans.price = open_price;
	trans.expiration = expiration;
	COPY_STR(trans.symbol, symbol);
	//--- check tick size
	if (ExtServer->TradesCheckTickSize(open_price, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: invalid price");
		return(0); // invalid price
	}
	//--- check secutiry
	if (ExtServer->TradesCheckSecurity(&symcfg, &grpcfg) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: trade disabled or market closed");
		return(0); // trade disabled, market closed, or no prices for long time
	}
	//--- check volume
	if (ExtServer->TradesCheckVolume(&trans, &symcfg, &grpcfg, TRUE) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: invalid volume");
		return(0); // invalid volume
	}
	//--- check allow expiration
	if ((grpcfg.rights&ALLOW_FLAG_EXPIRATION) == 0)
	if (expiration != 0)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: expiration denied");
		return(0); // expiration denied
	}
	//--- check stops
	if (ExtServer->TradesCheckStops(&trans, &symcfg, &grpcfg, NULL) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: invalid stops");
		return(0); // invalid stops
	}
	//--- get current prices
	if (ExtServer->HistoryPricesGroup(symbol, &grpcfg, curprices) != RET_OK)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: no prices for group");
		return(0); // error
	}
	//--- prepare new trade state of order
	trade.login = login;
	trade.cmd = cmd;
	trade.open_price = open_price;
	trade.volume = volume;
	trade.close_price = cmd == (OP_BUY_LIMIT || cmd == OP_BUY_STOP ? curprices[0] : curprices[1]);
	trade.open_time = ExtServer->TradeTime();
	//trade.spread = symcfg.spread;
	trade.digits = symcfg.digits;
	COPY_STR(trade.symbol, symbol);
	//--- fill SL,TP,expiration, comment
	//--- open pending
	if ((order = ExtServer->OrdersAdd(&trade, &info, &symcfg)) == 0)
	{
		Out(CmdErr, "FXPlugin", "OrdersAddOpenPending: OrdersAdd failed");
		return(0); // error
	}
	//--- pending order opened: return order
	return(order);
}

//+------------------------------------------------------------------+
//| Activate pending order using OrdersUpdate                        |
//+------------------------------------------------------------------+
int CProcessor::OrdersUpdateActivate(const int order, const double open_price)
{
	UserInfo       info = { 0 };
	ConSymbol      symcfg = { 0 };
	ConGroup       grpcfg = { 0 };
	TradeTransInfo trans = { 0 };
	TradeRecord    oldtrade = { 0 }, trade = { 0 };
	double         profit = 0, margin = 0, freemargin = 0, prevmargin = 0;
	//--- checks
	if (order <= 0 || open_price <= 0 || ExtServer == NULL) return(FALSE);
	//--- get order
	if (ExtServer->OrdersGet(order, &oldtrade) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: OrdersGet failed");
		return(FALSE); // error
	}
	//--- check state
	if (oldtrade.cmd<OP_BUY_LIMIT || oldtrade.cmd>OP_SELL_STOP)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: order already activated");
		return(FALSE);  // order already activated
	}
	//--- get user info
	if (UserInfoGet(oldtrade.login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: UserInfoGet failed");
		return(FALSE); // error
	}
	//--- get group config
	if (ExtServer->GroupsGet(info.group, &grpcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: GroupsGet failed [%s]", info.group);
		return(FALSE); // error
	}
	//--- get symbol config
	if (ExtServer->SymbolsGet(oldtrade.symbol, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: SymbolsGet failed [%s]", oldtrade.symbol);
		return(FALSE); // error
	}
	//--- check tick size
	if (ExtServer->TradesCheckTickSize(open_price, &symcfg) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: invalid price");
		return(0); // invalid price
	}
	//--- check close only
	if (symcfg.trade == TRADE_CLOSE)
	{
		//--- no enough margin
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: close only allowed");
		//---- delete order
		memcpy(&trade, &oldtrade, sizeof(TradeRecord));
		trade.close_time = ExtServer->TradeTime();
		trade.profit = 0;
		trade.storage = 0;
		trade.expiration = 0;
		trade.taxes = 0;
		COPY_STR(trade.comment, "deleted [close only]");
		//---- delete order
		if (ExtServer->OrdersUpdate(&trade, &info, UPDATE_CLOSE) == FALSE)
			Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: OrdersUpdate failed");
		//--- close only: order deleted
		return(FALSE);
	}
	//--- check margin
	margin = ExtServer->TradesMarginCheck(&info, &trans, &profit, &freemargin, &prevmargin);
	if ((freemargin + grpcfg.credit)<0 && (symcfg.margin_hedged_strong != FALSE || prevmargin <= margin))
	{
		//--- no enough margin
		Out(CmdErr, "FXPlugin", "OrdersAddPosition: not enough margin");
		//---- delete order
		memcpy(&trade, &oldtrade, sizeof(TradeRecord));
		trade.close_time = ExtServer->TradeTime();
		trade.profit = 0;
		trade.storage = 0;
		trade.expiration = 0;
		trade.taxes = 0;
		COPY_STR(trade.comment, "deleted [no money]");
		//---- delete order
		if (ExtServer->OrdersUpdate(&trade, &info, UPDATE_CLOSE) == FALSE)
			Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: OrdersUpdate failed");
		//--- no enough margin: order deleted
		return(FALSE);
	}
	//--- prepare new trade state of order
	memcpy(&trade, &oldtrade, sizeof(TradeRecord));
	trade.cmd = (oldtrade.cmd == OP_BUY_LIMIT || oldtrade.cmd == OP_BUY_STOP) ? OP_BUY : OP_SELL;
	trade.open_time = ExtServer->TradeTime();
	trade.open_price = open_price;
	trade.profit = 0;
	trade.storage = 0;
	trade.expiration = 0;
	trade.taxes = 0;
	//--- calc commission
	ExtServer->TradesCommission(&trade, info.group, &symcfg);
	//--- calc profit
	ExtServer->TradesCalcProfit(info.group, &trade);
	//--- calc conv rates and margin rates
	trade.conv_rates[0] = ExtServer->TradesCalcConvertation(info.group, FALSE, trade.open_price, &symcfg);
	trade.margin_rate = ExtServer->TradesCalcConvertation(info.group, TRUE, trade.open_price, &symcfg);
	//--- activate order
	if (ExtServer->OrdersUpdate(&trade, &info, UPDATE_ACTIVATE) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateActivate: OrdersUpdate failed");
		return(FALSE); // error
	}
	//--- pending order activated
	return(TRUE);
}

//+------------------------------------------------------------------+
//| Cancel pending order using OrdersUpdate                          |
//+------------------------------------------------------------------+
int CProcessor::OrdersUpdateCancel(const int order)
{
	UserInfo    info = { 0 };
	TradeRecord oldtrade = { 0 }, trade = { 0 };
	//--- checks
	if (order <= 0 || ExtServer == NULL) return(FALSE);
	//--- get order
	if (ExtServer->OrdersGet(order, &oldtrade) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateDelete: OrdersGet failed");
		return(FALSE); // error
	}
	//--- check
	if (oldtrade.cmd<OP_BUY_LIMIT || oldtrade.cmd>OP_SELL_STOP)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateDelete: order already activated");
		return(FALSE); // order already activated
	}
	//--- check
	if (oldtrade.close_time != 0)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateDelete: order already deleted");
		return(FALSE); // order already deleted
	}
	//--- get user info
	if (UserInfoGet(oldtrade.login, &info) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateDelete: UserInfoGet failed");
		return(FALSE); // error
	}
	//--- prepare new trade state of order
	memcpy(&trade, &oldtrade, sizeof(TradeRecord));
	trade.close_time = ExtServer->TradeTime();
	trade.profit = 0;
	trade.storage = 0;
	trade.expiration = 0;
	trade.taxes = 0;
	//--- fill comment
	//--- delete pending
	if (ExtServer->OrdersUpdate(&trade, &info, UPDATE_CLOSE) == FALSE)
	{
		Out(CmdErr, "FXPlugin", "OrdersUpdateClose: UserInfoGet failed");
		return(FALSE); // error
	}
	//--- pending order deleted
	return(TRUE);
}

bool CProcessor::CheckLogin(int login, const char* Password)
{
	ExtServer->LogsOut(CmdOK, "FXPlugin", "CheckLogin");
	UserRecord user = { 0 };
	//---- checks
	if (login < 1 || ExtServer == NULL || Password == NULL)
		return(FALSE);
	//---- get user record
	if (ExtServer->ClientsUserInfo(login, &user) == FALSE)
		return(FALSE);
	if (strcmp(user.group, "manager") || !user.enable)
		return(FALSE);
	if (ExtServer->ClientsCheckPass(login, Password, FALSE) == FALSE)
	{
		return FALSE;
	}
	return TRUE;
}

//+------------------------------------------------------------------+
//| Thread wrapper                                                   |
//+------------------------------------------------------------------+
UINT __stdcall CProcessor::ThreadWrapper(LPVOID pParam)
{
	//---
	if (pParam != NULL) ((CProcessor*)pParam)->ThreadProcess();
	//---
	return(0);
}

//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CProcessor::ThreadProcess(void)
{
	WSADATA       wsa;
	//--- just a banner
	printf("Server Emulator for quotes and trades\n");
	printf("Copyright 2001-2014, MetaQuotes Software Corp.\n\n");
	//--- initialized winsock 2
	if (WSAStartup(0x0202, &wsa) != 0)
	{
		printf("Winsock initialization failed\n");
		return;
		//return(-1);
	}
	m_socketServer = new CSocketServer(*this);
	//--- start the server
	m_socketServer->Initialize();
	//if (m_socketServer->StartServer(4444) == FALSE) return(-1);
	if (m_socketServer->StartServer(4444) == FALSE) return;
	printf("Press any key to stop server...\n");
	//--- wait untill finished
	while (m_socketServer->Finished() == FALSE)
	{
		Sleep(500);
		//if (_kbhit()) { _getch(); break; }
	}
	delete m_socketServer;
	m_socketServer = NULL;
	//m_socketServer->Shutdown();
	//--- finish
	WSACleanup();
}