// This is the main DLL file.

#include "stdafx.h"
#include "MT4ManWrapper.h"
using namespace MT4;
using namespace System::Runtime::InteropServices;
using namespace System::Timers;
////////////////////////////////////////
// MT4Manager [Managed]
///////////////////////////////////////
MT4Manager::MT4Manager()
{
	isDisposed = false;
    nativeWrapper = new MT4NativeWrapper(this);
	keepAliveTimer = gcnew System::Timers::Timer( 30 * 1000 );
	keepAliveTimer->Elapsed += gcnew ElapsedEventHandler(this,&MT4Manager::KeepAliveTimer_Tick );
}

void MT4Manager::CleanUp(bool disposing)
{
	if(nativeWrapper != nullptr)
	{
		nativeWrapper->Disconnect();
	}
}

void MT4Manager::SendError(String ^ msg)
{
	OnNewMsg(msg,1);
}
void MT4Manager::SendMsg(String ^ msg)
{
	OnNewMsg(msg,0);
}

void MT4Manager::OnNativeConnectionChanged(int type,bool bConnected)
{
	OnConnectionChanged(type,bConnected);
}

void MT4Manager::OnLog(char * msg)
{
    String ^ newMsg = gcnew String(msg);
	OnNewMsg(newMsg,0);
}

void MT4Manager::OnError(char * msg)
{
	String ^ newMsg = gcnew String(msg);
	OnNewMsg(newMsg,1);
}

DateTime ^ MT4Manager::ParseTime(time_t tt)
{
	DateTime ^ dt = DateTime(1970,1,1,0,0,0).AddSeconds(tt);
	return dt;
}

DateTime ^ MT4Manager::GetServerTime()
{
	if(nativeWrapper == NULL)
		return DateTime(1970, 1, 1);
		
	time_t date = nativeWrapper->GetServerTime();
	double msec = static_cast<double>(date);
    return DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind::Utc).AddSeconds(msec);
}

bool MT4Manager::Connect(String ^ _serverAddress,int _login,String ^ _password)
{
	IntPtr addr = Marshal::StringToHGlobalAnsi(_serverAddress);
	char* addrStr = static_cast<char*>(addr.ToPointer());

	IntPtr psw = Marshal::StringToHGlobalAnsi(_password);
	char* pswStr = static_cast<char*>(psw.ToPointer());

	bool bConnected = false;
	OnNewMsg("Connecting to server " + _serverAddress + "...",0);
	if(nativeWrapper != NULL)
		bConnected = nativeWrapper->Connect(addrStr,_login,pswStr);
	Marshal::FreeHGlobal( addr );
	Marshal::FreeHGlobal( psw );

    keepAliveTimer->Start();
	return bConnected;
}

bool MT4Manager::Disconnect()
{
	bool bConnected = false;
	if(nativeWrapper != NULL)
		bConnected = nativeWrapper->Disconnect();
	keepAliveTimer->Stop();
	return  bConnected;
}

void MT4Manager::OnOrderOpened(TradeRecord * trade,int type)
{
	try
	{
		if(trade != NULL)
		{
			OrderInfo ^ oInfo = ParseOpenTrade(trade);
			OnOpenTrade(oInfo);
		}
	}
	catch(Exception ^ ex)
	{
		SendError(ex->Message);
	}
}

void MT4Manager::OnOrderClosed(TradeRecord * trade,int type)
{
	try
	{
		if(trade != NULL)
		{
			OrderInfo ^ oInfo = ParseClosedTrade(trade);
			OnCloseTrade(oInfo);
		}
	}
	catch(Exception ^ ex)
	{
		SendError(ex->Message);
	}
}

int MT4Manager::OpenOrder(int login,OrderSide side,String ^ symbol,int volume,double openPrice,String ^ comment,String ^%errMsg,DateTime %openTime)
{
	try
	{
		int cmd = 0;
		char errorMsg[1000]={0};
		if(side == OrderSide::SELL)
			cmd = 1;
		IntPtr sym = Marshal::StringToHGlobalAnsi(symbol);
		char* symStr = static_cast<char*>(sym.ToPointer());
		
		IntPtr cmt = Marshal::StringToHGlobalAnsi(comment);
		char* cmtStr = static_cast<char*>(cmt.ToPointer());
		
		long openTimeSec = 0;
		int ticket = nativeWrapper->OpenOrder(login,cmd,symStr,volume,openPrice,cmtStr,errorMsg,&openTimeSec);
		if(ticket < 0)
		{
			errMsg = gcnew String(errorMsg);
			openTime = DateTime::MinValue;
		}
		else
		{
			DateTime ^ baseTime = gcnew DateTime(1970,1,1);
			openTime = baseTime->AddSeconds(openTimeSec);
		}

		Marshal::FreeHGlobal( sym );
		Marshal::FreeHGlobal( cmt );
		return ticket;
	}
	catch(Exception ^ ex)
	{
		SendError(ex->Message);
		return -1;
	}
}


//return -1 = fail , 0 = succeeded , > 0  = partial close new ticket
int MT4Manager::CloseOrder(int _ticket,OrderSide side,String ^ symbol,int volume,double closePrice,String ^ comment,bool bPartial,String ^ %errMsg,DateTime %closeTime)
{
	try
	{
		int returnVal = -1;
		int cmd = 0;
		char errorMsg[1000]={0};
		if(side == OrderSide::SELL)
			cmd = 1;
		IntPtr sym = Marshal::StringToHGlobalAnsi(symbol);
		char* symStr = static_cast<char*>(sym.ToPointer());
		
		IntPtr cmt = Marshal::StringToHGlobalAnsi(comment);
		char* cmtStr = static_cast<char*>(cmt.ToPointer());

		long closeTimeSec = 0;
		bool res = nativeWrapper->CloseOrder(_ticket,cmd,symStr,volume,closePrice,cmtStr,errorMsg,bPartial,&closeTimeSec);
		if(res == false)
		{
			errMsg = gcnew String(errorMsg);
			closeTime = DateTime::MinValue;
		}
		else
		{
			//get the order close time
			DateTime ^ baseTime = gcnew DateTime(1970,1,1);
			closeTime = baseTime->AddSeconds(closeTimeSec);
			returnVal = 0;
			if(bPartial)
			{
				String ^ comment = gcnew String(cmtStr);
				//check partial close new ticket by parsing the comment (look for 'to #ticket')
				int index = comment->IndexOf("to #");
				if(index != -1)
				{
					int ticketNum;
					String ^ ticket = comment->Substring(index + 4);//move to the end of the "to #"
					if (int::TryParse(ticket,ticketNum))
						returnVal = ticketNum;
				}
			}
		}
		Marshal::FreeHGlobal( sym );
		Marshal::FreeHGlobal( cmt );
		return returnVal;
	}
	catch(Exception ^ ex)
	{
		SendError(ex->Message);
		return -1;
	}
}

bool MT4Manager::CreateUser(int login,String ^ _name,String ^ _psw,String ^ group,int deposit)
{
	bool bCreated = false;
	IntPtr name = Marshal::StringToHGlobalAnsi(_name);
	char* nameStr = static_cast<char*>(name.ToPointer());

	IntPtr psw = Marshal::StringToHGlobalAnsi(_psw);
	char* pswStr = static_cast<char*>(psw.ToPointer());

	IntPtr grp = Marshal::StringToHGlobalAnsi(group);
	char* grpStr = static_cast<char*>(grp.ToPointer());

	if(nativeWrapper != NULL)
		if(nativeWrapper->CreateAccount(login,nameStr,pswStr,grpStr,deposit))
			bCreated = true;

	Marshal::FreeHGlobal( name );
	Marshal::FreeHGlobal( psw );
	Marshal::FreeHGlobal( grp );

	return bCreated;
}

String ^ MT4Manager::GetUserName(int login)
{
	char * userName = nativeWrapper->GetUserName(login);
	String ^ nameStr = gcnew String("");
	if(userName != NULL)
		nameStr = gcnew String(userName);
	return nameStr;
}

UserDetails ^ MT4Manager::GetUser(int login)
{
	UserRecord * user = nativeWrapper->GetUser(login);
	if(user != NULL)
    {
        UserDetails ^ user_details = gcnew UserDetails();
        user_details->Login = login;
		user_details->Group = (gcnew String(user->group))->ToUpper();
        user_details->Leverage = user->leverage;
        user_details->Balance = user->balance;
        user_details->Credit = user->credit;
        user_details->Name = gcnew String(user->name);

		MarginLevel * marginLevel = nativeWrapper->GetUserMarginLevel(login);
		if(marginLevel != NULL)
		{
			user_details->Equity = marginLevel->equity;
			//nativeWrapper->FreeMem(marginLevel);
		}

		nativeWrapper->FreeUserMem(user);
	    return user_details;
    }
    return nullptr;
}

OrderInfo ^ MT4Manager::ParseClosedTrade(TradeRecord * trade)
{
	if(trade != NULL && (trade->cmd == 0 || trade->cmd == 1) )
	{
		OrderInfo ^ oInfo = gcnew OrderInfo();
		oInfo->Ticket = trade->order;
		oInfo->Login = trade->login;
		oInfo->Symbol = gcnew String(trade->symbol);
		oInfo->Volume = trade->volume;
		if(trade->cmd == 0)
			oInfo->Side = OrderSide::BUY;
		else if(trade->cmd == 1)
			oInfo->Side = OrderSide::SELL;
		else
			oInfo->Side = OrderSide::UNKNOWN;
		oInfo->Price =  Math::Round(trade->close_price,5);
		oInfo->Time = ParseTime(trade->close_time);
		oInfo->Comment = gcnew String(trade->comment);
		
		//check if its a partial close by parsing the comment (look for 'to #ticket')
		int index = oInfo->Comment->IndexOf("to #");
		if(index != -1)
        {
			int ticketNum;
            String ^ ticket = oInfo->Comment->Substring(index + 4);//move to the end of the "to #"
			if (int::TryParse(ticket,ticketNum))
                 oInfo->NewTicket = ticketNum;
        }
		/*
		UserRecord * user = nativeWrapper->GetUser(oInfo->Login);
		if(user != NULL)
		{
			oInfo->Group = (gcnew String(user->group))->ToUpper();

			nativeWrapper->FreeUserMem(user);
		}
		*/
		return oInfo;
	}
	return nullptr;
}

OrderInfo ^ MT4Manager::ParseOpenTrade(TradeRecord * trade)
{
	if(trade != NULL && (trade->cmd == 0 || trade->cmd == 1) )
	{
		OrderInfo ^ oInfo = gcnew OrderInfo();
		oInfo->Ticket = trade->order;
		oInfo->Login = trade->login;
		oInfo->Symbol = gcnew String(trade->symbol);
		oInfo->Volume = trade->volume;
		oInfo->Price = Math::Round(trade->open_price,5);
		if(trade->cmd == 0)
			oInfo->Side = OrderSide::BUY;
		else if(trade->cmd == 1)
			oInfo->Side = OrderSide::SELL;
		else
			oInfo->Side = OrderSide::UNKNOWN;
		oInfo->Time = ParseTime(trade->open_time);
		oInfo->Comment = gcnew String(trade->comment);
		oInfo->NewTicket = -1;

		UserRecord * user = nativeWrapper->GetUser(oInfo->Login);
		if(user != NULL)
		{
			oInfo->Group = (gcnew String(user->group))->ToUpper();

			nativeWrapper->FreeUserMem(user);
		}
		else
		{
			oInfo->Group = (gcnew String(""));
		}
		return oInfo;
	}
	return nullptr;
}

//GET ALL THE TRADES OPEN/CLOSED FROM THE SPECIFIC TIME TILL NOW
//and return them in two seperate dictionaries one for the open trades and one for the closed
bool MT4Manager::GetTrades(String ^ groups,List<int>^ logins,DateTime^ sinceTime,Dictionary<int,List<OrderInfo^>^>^ %openDic,Dictionary<int,List<OrderInfo^>^>^ %closeDic)
{
	//
	UserRecord * users = NULL;
	if(!String::IsNullOrEmpty(groups)){
		char* groupsStr = static_cast<char*>(Marshal::StringToHGlobalAnsi(groups).ToPointer());
		int userTotal = nativeWrapper->GetAllUsers(groupsStr,&users);
		if(users != NULL){
			for(int j = 0 ; j < userTotal ; j++){
				logins->Add(users[j].login);
			}
			nativeWrapper->FreeUserMem(users);
		}
	}

	//Get All Open Trades and add them to the open dictionary
	TradeRecord * openTrades = NULL;
	int total = nativeWrapper->GetOpenTrades(&openTrades);
	if(openTrades != NULL)
	{
		for(int i = 0 ; i < total ; i++)
		{
			//if the trade is a market command and belongs to one of the specified logins
			if((openTrades[i].cmd == 0 || openTrades[i].cmd == 1 ) && logins->Contains(openTrades[i].login))
			{
				OrderInfo ^ oInfo = ParseOpenTrade(&openTrades[i]);
				if(oInfo != nullptr && ( (oInfo->Time->CompareTo(sinceTime)) >= 0 ) )
				{
					if(!openDic->ContainsKey(openTrades[i].login))
						openDic[openTrades[i].login] = gcnew List<OrderInfo^>();
					openDic[openTrades[i].login]->Add(oInfo);
				}
			}
		}
		nativeWrapper->FreeTradesMem(openTrades);
	}
	//get all the close trades and add them to the close dictionary (if there are trades that opened after the start time, add them to the open dictionary too)
	DateTime baseTime(1970, 1, 1);
	DateTime nowTime = DateTime::Now.AddDays(1); //adding some minutes ahead to the future to deal with difference in computer time 
	time_t startTime = (int)(sinceTime->Subtract(baseTime).TotalSeconds);
	time_t endTime = (int)((nowTime.Subtract(baseTime)).TotalSeconds);
	TradeRecord * closedTrades = NULL;
	
	//for each login
	for each(int login in logins)
	{
		total = nativeWrapper->GetClosedTrades(&closedTrades,login,startTime,endTime);
		if(closedTrades != NULL)
		{
			for(int i = 0 ; i < total ; i++)
			{
				//if the trade belongs to one of the specified logins
				if((closedTrades[i].cmd == 0 || closedTrades[i].cmd == 1 ) && logins->Contains(closedTrades[i].login))
				{
					//now we need to check if the closed order was opened after our start time
					//if it did we need to add it to the open trades dictionary and in addition to the closed one

					DateTime ^ tradeOpenTime = ParseTime(closedTrades[i].open_time);

					//add it to the open trades dictionary
					if( (tradeOpenTime->Subtract((DateTime)sinceTime)).TotalSeconds >= 0)
					{
						OrderInfo ^ oInfo = ParseOpenTrade(&closedTrades[i]);
						if(!openDic->ContainsKey(oInfo->Login))
							openDic[oInfo->Login] = gcnew List<OrderInfo^>();
						openDic[oInfo->Login]->Add(oInfo);
					}
					OrderInfo ^ oInfo = ParseClosedTrade(&closedTrades[i]);
					if(!closeDic->ContainsKey(oInfo->Login))
						closeDic[oInfo->Login] = gcnew List<OrderInfo^>();
					closeDic[oInfo->Login]->Add(oInfo);
				}
			}
			nativeWrapper->FreeTradesMem(closedTrades);
		}
	}
	return true;
}

//GET ALL THE OPEN TRADES
bool MT4Manager::GetOpenTrades(List<int>^ logins,Dictionary<int,List<OrderInfo^>^>^ %openDic)
{
	//Get All Open Trades and add them to the open dictionary
	TradeRecord * openTrades = NULL;
	int total = nativeWrapper->GetOpenTrades(&openTrades);
	if(openTrades != NULL)
	{
		for(int i = 0 ; i < total ; i++)
		{
			//if the trade is a market command and belongs to one of the specified logins
			if((openTrades[i].cmd == 0 || openTrades[i].cmd == 1 ) && logins->Contains(openTrades[i].login))
			{
				OrderInfo ^ oInfo = ParseOpenTrade(&openTrades[i]);
				if(oInfo != nullptr)
				{
					if(!openDic->ContainsKey(openTrades[i].login))
						openDic[openTrades[i].login] = gcnew List<OrderInfo^>();
					openDic[openTrades[i].login]->Add(oInfo);
				}
			}
		}
		nativeWrapper->FreeTradesMem(openTrades);
	}

	return true;
}

bool MT4Manager::GetOpenTrades(String ^ groups,Dictionary<int,List<OrderInfo^>^>^ %openDic)
{
	//
	if(!String::IsNullOrEmpty(groups)){
		char* groupsStr = static_cast<char*>(Marshal::StringToHGlobalAnsi(groups).ToPointer());
		TradeRecord * openTrades = NULL;
		int total = nativeWrapper->GetOpenTradesByGroup(groupsStr,&openTrades);
		if(openTrades != NULL)
		{
			for(int i = 0 ; i < total ; i++)
			{
				//if the trade is a market command and belongs to one of the specified logins
				if((openTrades[i].cmd == 0 || openTrades[i].cmd == 1 ))
				{
					OrderInfo ^ oInfo = ParseOpenTrade(&openTrades[i]);
					if(oInfo != nullptr)
					{
						if(!openDic->ContainsKey(openTrades[i].login))
							openDic[openTrades[i].login] = gcnew List<OrderInfo^>();
						openDic[openTrades[i].login]->Add(oInfo);
					}
				}
			}
			nativeWrapper->FreeTradesMem(openTrades);
		}
	}

	//Get All Open Trades and add them to the open dictionary
	return true;
}




///////////////////////////////////////////////////
// MT4NativeWrapper [Unmanaged]
///////////////////////////////////////////////////

void __stdcall NotifyCallBack(int code,int type,void* data,void *param)
{
    MT4NativeWrapper * pThis = (MT4NativeWrapper*) param;
    //--- checks
    if(code ==  PUMP_START_PUMPING)
    {
        pThis->OnPumpConnectionChanged(true);
        return;
    }
         
    if(code == PUMP_STOP_PUMPING)
    {
        pThis->OnPumpConnectionChanged(false);
        //pThis->OnLog("Stop Pumping");
        return;
    }
    
    if(code==PUMP_UPDATE_TRADES && data!=NULL)
    {
        TradeRecord *trade=(TradeRecord*)data;
        //only open trades (without pendings);
        if(trade->cmd < 2)
        {
			//if(type == TRANS_ADD || type== TRANS_DELETE)
			if(type == TRANS_ADD || type== TRANS_DELETE || (trade->login != 0 && type == TRANS_UPDATE))
				pThis->OnTrade(trade,type);
        }
    }
}


//pump connection status changed
void MT4NativeWrapper::OnPumpConnectionChanged(bool bConnected)
{
	this->m_ManagedWrapper->OnNativeConnectionChanged(0,bConnected);
}

//Direct connection status changed
void MT4NativeWrapper::OnDirectConnectionChanged(bool bConnected)
{
	this->m_ManagedWrapper->OnNativeConnectionChanged(1,bConnected);
}

bool MT4NativeWrapper::DisconnectDirect()
{
	int res;

	//disconnect direct manager if needed
    this->m_ManagedWrapper->OnLog("Disconnecting Direct...");
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			if(directManager.Manager_ReCreate())
			{
				this->m_ManagedWrapper->OnLog("Direct disconnected successfuly");
			}
			else
			{
				this->m_ManagedWrapper->OnLog("Direct disconnect failed");
			}
			OnDirectConnectionChanged(false);
			return true;
			/*
			if((res = directManager->Disconnect())== RET_OK)
			{
				this->m_ManagedWrapper->OnLog("Direct disconnected successfuly");
				OnDirectConnectionChanged(false);
				return true;
			}
			else
			{
				this->m_ManagedWrapper->OnError("Direct disconnect failed (error will follow):");
				this->m_ManagedWrapper->OnError((char *)directManager->ErrorDescription(res));
			}*/
		}
		else
		{
			this->m_ManagedWrapper->OnLog("Disconnect Direct aborted, direct manager already disconnected");
		}
	}
	else
	{
		this->m_ManagedWrapper->OnLog("Disconnect Direct aborted, direct manager not valid");
	}
	return false;
}

bool MT4NativeWrapper::DisconnectPump()
{
	int res;
	//disconnect pump manager if needed
	this->m_ManagedWrapper->OnLog("Disconnecting Pump...");
	if(pumpManager.IsValid())
	{
		if(pumpManager->IsConnected())
		{
			if(pumpManager.Manager_ReCreate())
			{
				this->m_ManagedWrapper->OnLog("Pump disconnected successfuly");
			}
			else
			{
				this->m_ManagedWrapper->OnLog("Pump disconnect failed");
			}
			/*
			if((res = pumpManager->Disconnect()) == RET_OK)
			{
				this->m_ManagedWrapper->OnLog("Pump disconnected successfuly");
				return true;
			}
			else
			{
				this->m_ManagedWrapper->OnError("Pump disconnect failed (error will follow):");
				this->m_ManagedWrapper->OnError((char *)pumpManager->ErrorDescription(res));
			}
			*/
		}
		else
		{
			this->m_ManagedWrapper->OnLog("Disconnect Pump aborted, Pump manager already disconnected");
		}
	}
	else
	{
		this->m_ManagedWrapper->OnLog("Disconnect Pump aborted, Pump manager not valid");
	}
	return false;
}

bool MT4NativeWrapper::Disconnect()
{
	bShouldBeConnected = false;
	InnerDisconnect();
	return(true);
}

bool MT4NativeWrapper::InnerDisconnect()
{
	this->m_ManagedWrapper->OnLog("Disconnecting...");
	DisconnectDirect();
	DisconnectPump();
	return(true);
}

bool MT4NativeWrapper::Connect(char * _serverAddress,int _login,char * _psw)
{
	//clean memory of previous data
	if(serverAddress != NULL)
	{
		delete [] serverAddress;
		serverAddress = NULL;
	}
	if(psw != NULL)
	{
		delete [] psw;
		psw = NULL;
	}

	//copy the connection  parameter to local variables
	serverAddress = new char[strlen(_serverAddress) + 1];
	strcpy_s(serverAddress,strlen(_serverAddress) + 1,_serverAddress);
	login = _login;
	psw =  new char[strlen(_psw) + 1];
	strcpy_s(psw,strlen(_psw) + 1,_psw);

	//set the flag to true to indicate we should be connected to the serve
	bShouldBeConnected = true;
	return InnerConnect();
}


bool MT4NativeWrapper::ConnectPump()
{
	this->m_ManagedWrapper->OnLog("Start connecting pump...");
	int res = -999;
	if(pumpManager.IsValid())
	{
		if(pumpManager->IsConnected())
		{
			this->m_ManagedWrapper->OnLog("Connecting pump aborted,pump manager is already connected");
			return false;
		}
		do
		{
			this->m_ManagedWrapper->OnLog("Connecting pump...");
			if((res = pumpManager->Connect(serverAddress))== RET_OK)
			{
				bPumpManagerConnected = true;
				this->m_ManagedWrapper->OnLog("Login pump...");
				if((res = pumpManager->Login(login,psw))==RET_OK)
				{
					this->m_ManagedWrapper->OnLog("Pump logged in successfuly");
					pumpManager->PumpingSwitchEx(NotifyCallBack,CLIENT_FLAGS_HIDETICKS|CLIENT_FLAGS_HIDENEWS|CLIENT_FLAGS_HIDEMAIL|CLIENT_FLAGS_HIDEONLINE|CLIENT_FLAGS_HIDEUSERS,this);
					return true;
				}
				else
				{
					this->m_ManagedWrapper->OnError("Pump logging in failed");
				}
			}
			else
			{
				this->m_ManagedWrapper->OnError("Pump connection failed (error will follow):");
				this->m_ManagedWrapper->OnError((char *)pumpManager->ErrorDescription(res));
			}
		}
		while(res != RET_OK);
		return false;
	}
	else
	{
		this->m_ManagedWrapper->OnError("Failed Connecting pump,pump manager is not valid");
		return false;
	}
}

bool MT4NativeWrapper::ConnectDirect()
{
	this->m_ManagedWrapper->OnLog("Start connecting direct...");
	int res = -999;
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			this->m_ManagedWrapper->OnLog("Connecting direct aborted,direct manager is already connected");
			return false;
		}
		do
		{
			this->m_ManagedWrapper->OnLog("Connecting direct...");
			if((res = directManager->Connect(serverAddress))==RET_OK)
			{
				bDirectManagerConnected = true;
				this->m_ManagedWrapper->OnLog("Login direct...");
				if( (res = directManager->Login(login,psw))==RET_OK)
				{
					this->m_ManagedWrapper->OnLog("Direct logged in successfuly");
					OnDirectConnectionChanged(true);
					return true;
				}
				else
				{
					this->m_ManagedWrapper->OnError("Direct logging in failed");
				}
			}
			else
			{
				this->m_ManagedWrapper->OnError("Direct connection failed (error will follow):");
				this->m_ManagedWrapper->OnError((char *)directManager->ErrorDescription(res));
			}
		}
		while(res != RET_OK);
		return false;
	}
	else
	{
		this->m_ManagedWrapper->OnError("Failed Connecting direct,direct manager is not valid");
		return false;
	}
}


bool MT4NativeWrapper::InnerConnect()
{
	bool res1,res2;

	/*if(bPumpManagerConnected || bDirectManagerConnected)
	{
		InnerDisconnect();
	}*/
	res1 = ConnectPump();
	res2 = ConnectDirect();
	if(res1 && res2)
		return true;
	return false;
}

void MT4NativeWrapper::KeepAlive()
{
	bool bDisconnected = false; // this flag tells us if we got disconnected from one of our two source connections (direct/pump)
	
	//if we should be connected (the user pressed connect and has not disconnect yet)
	if(bShouldBeConnected)
	{	
		//no need to check the pump connection as it notifies of disconnection by itself
		if(pumpManager.IsValid())
		{
			//if the pump manager is disconnected and it was connected before
			if(!pumpManager->IsConnected() && bPumpManagerConnected == true)
			{
				bDisconnected = true;
			}
		}
		else
		{
			if(bPumpManagerConnected)
				bDisconnected = true;
		}

		//on the other hand the direct connection should be ping from time to time to keep the connection alive,and should be checked for disconnection
	
		if(directManager.IsValid())
		{
			if(directManager->IsConnected()){
				directManager->Ping();
			}
			//check if not disconnected after the ping
			if(directManager->IsConnected())
			{
				OnDirectConnectionChanged(true);//notify of the connection is connected
			}
			else
			{
				bDirectManagerConnected = false;
				OnDirectConnectionChanged(false);//notify of the connection status changed
			}
		}
		else
		{
			if(bDirectManagerConnected)
				bDisconnected = true;
		}

		//if we got disconnected - reconnect
		if(bDisconnected || !bDirectManagerConnected || !bPumpManagerConnected)
		{
			InnerConnect(); //and reconnect again
		}
	}
}

bool MT4NativeWrapper::IsConnected()
{
	if(!pumpManager.IsValid()) { return false; }
	if(pumpManager->IsConnected()==FALSE) { return false; }
	if(!directManager.IsValid()) { return false; }
	directManager->Ping();
	if(directManager->IsConnected()==FALSE) { return false; }
	return true;
}

void MT4NativeWrapper::OnLog(char * msg)
{
    this->m_ManagedWrapper->OnLog(msg);
}

void MT4NativeWrapper::OnError(char * msg)
{
	this->m_ManagedWrapper->OnError(msg);
}

void MT4NativeWrapper::OnTrade(TradeRecord * trade,int type)
{
	 switch(type)
	 {
		 case TRANS_ADD:
		 case TRANS_UPDATE:
			 this->m_ManagedWrapper->OnOrderOpened(trade,type);
			 break;
		 case TRANS_DELETE:
			this->m_ManagedWrapper->OnOrderClosed(trade,type);
			 break;
	 }
}

int MT4NativeWrapper::OpenOrder(int login,int cmd,char * symbol,int volume,double openPrice,char * comment,char * errMsg,long * openTime)
{
	int res=RET_ERROR;

	*openTime = 0; //initialize the returned open time value
	TradeTransInfo info  ={0};
	info.type   =TT_BR_ORDER_OPEN;      // trade transaction type
    info.cmd    =cmd;                // trade command
    info.orderby=login;                 // order, order by, login
    strcpy_s(info.symbol,12,symbol);       // trade symbol
    info.volume = volume;                   // trade volume
    info.price  =openPrice;                // trade price
	strncpy_s(info.comment,32,comment,31);
    res = directManager->TradeTransaction(&info);
	if(res != RET_OK && res != RET_TRADE_ACCEPTED)
    {
		LPCSTR err  = directManager->ErrorDescription(res);
        sprintf(errMsg,"%d(%s)",res,err);
		//strcpy(errMsg,err);
	    //return -1;
    }
    if(info.order > 0)
    {
		int total = 1;
		//if the order was opened succesfully - get the open time of the trade and return it in the output parameter
		TradeRecord* newRecord =  directManager->TradeRecordsRequest(&info.order,&total);
		if(newRecord != NULL)
		{
			*openTime = newRecord->open_time;
			FreeTradesMem(newRecord);
		    errMsg = NULL;
		}
	    return info.order;
    }
	else if(res == RET_OK)
	{
		sprintf(errMsg,"%d(OK)",res);
		return -1;
	}
	else if(res == RET_TRADE_ACCEPTED)
	{
		return -2;
	}
	else if(res == RET_NO_CONNECT)
	{
		return -3;
	}
	else if(res == RET_TRADE_NO_MONEY)
	{
		return -4;
	}
	else if(res == RET_TRADE_BAD_VOLUME)
	{
		return -5;
	}
	else if(res == RET_INVALID_DATA)
	{
		return -6;
	}
    else
    {
        return -1;
    }
}

bool MT4NativeWrapper::CloseOrder(int ticket,int cmd,char * symbol,int volume,double closePrice,char * comment,char * errMsg,bool bIsPartial,long * closeTime)
{
	int res=RET_ERROR;
	*closeTime = 0; //initialize the returned open time value

  	int test_total = 1;
  	int test_ticket = ticket;
	TradeRecord* test_newRecord =  directManager->TradeRecordsRequest(&test_ticket,&test_total);
	if(test_newRecord != NULL)
	{
		long test_time = test_newRecord->close_time;
        if(test_time > 0){
            *closeTime = test_time;
            if(bIsPartial){
                strncpy_s(comment,31,test_newRecord->comment,31);
            }
        }
		FreeTradesMem(test_newRecord);
        if(test_time > 0){ 
            return true;
        }
	}
    else{
        sprintf(errMsg,"Ticket not found: %d",ticket);
        return false;
    }

	TradeTransInfo info  ={0};
	info.type   =TT_BR_ORDER_CLOSE;      // trade transaction type
    info.cmd    =cmd;   // trade command
	info.order = ticket;
    strcpy_s(info.symbol,12,symbol);       // trade symbol
    info.volume =volume;                   // trade volume
	info.price = closePrice;
    strncpy_s(info.comment,32,comment,31);
    res = directManager->TradeTransaction(&info);
	if(res != RET_OK)
    {
		LPCSTR err  = directManager->ErrorDescription(res);
        sprintf(errMsg,"%d(%s)",res,err);
	    //strcpy(errMsg,err);
	    //return false;
    }
    //else
    //{
		//if the order was closed succesfully - get the close time of the trade and return it in the output parameter
		int total = 1;
		TradeRecord* newRecord =  directManager->TradeRecordsRequest(&ticket,&total);
		if(newRecord != NULL)
		{
			*closeTime = newRecord->close_time;
		    //if it's a partial close - get the new ticket from the trade comment
            if(bIsPartial){
                strncpy_s(comment,31,newRecord->comment,31);
            }
			FreeTradesMem(newRecord);
		}
        else{
            sprintf(errMsg,"Ticket not found: %d",ticket);
            return false;
        }
		return true;
    //}
}

//as the pump connection is automatically notifying on his connection state
//we check only the direct manager connection (tradeManager) for connectivity status
bool MT4NativeWrapper::CreateAccount(int login,char * name,char * psw,char * group,int deposit)
{
	int res =RET_ERROR;

	if(bDirectManagerConnected)
	{
		UserRecord user = {0};
		
		user.login = login;
		strcpy_s(user.password,16,psw);
		strcpy_s(user.name,128,name);
		strcpy_s(user.group,16,group);
		user.enable = 1;
		user.leverage = 100;
		user.balance = deposit;
		user.enable_change_password = 1;
		user.user_color  =USER_COLOR_NONE;

		res=directManager->UserRecordNew(&user);
	
		if(res == RET_OK)
		{
			//now we will deposit money to the account
			TradeTransInfo info={0};
			
			info.type   =TT_BR_BALANCE;
			info.cmd    =OP_BALANCE;
			info.orderby=login;
			info.price=deposit;
			strcpy_s(info.comment,32,"By Api");
			res=directManager->TradeTransaction(&info);
			if(res == RET_OK)
				return true;
		}
	}
	return false;
}

char * MT4NativeWrapper::GetUserName(int login)
{
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			UserRecord * user = NULL;
			int total = 1;
			user = directManager->UserRecordsRequest(&login,&total);
			if(user != NULL)
				return user->name;
		}
	}
	return NULL;
}

UserRecord * MT4NativeWrapper::GetUser(int login)
{
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			int total = 1;
			return directManager->UserRecordsRequest(&login,&total);
		}
	}
	return NULL;
}

int MT4NativeWrapper::GetOpenTrades(TradeRecord ** trades)
{
	int total = 0;
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			*trades = directManager->TradesRequest(&total);
		}
	}
	return total;
}

int MT4NativeWrapper::GetAllUsers(char * group,UserRecord ** users)
{
	int total = 0;
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			//*users = directManager->UsersRequest(&total);
			*users = directManager->AdmUsersRequest(group,&total);
		}
	}
	return total;
}

int MT4NativeWrapper::GetOpenTradesByGroup(char * group,TradeRecord ** trades)
{
	int total = 0;
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			*trades = directManager->AdmTradesRequest(group,TRUE,&total);
		}
	}
	return total;
}

int MT4NativeWrapper::GetClosedTrades(TradeRecord ** trades,int login,time_t from,time_t to)
{
	int total = 0;
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			*trades=directManager->TradesUserHistory(login,from,to,&total);
		}
	}
	return total;
}

void MT4NativeWrapper::FreeTradesMem(TradeRecord * trades)
{
	if(trades != NULL && directManager.IsValid())
	{
		directManager->MemFree(trades);
	}
}

void MT4NativeWrapper::FreeUserMem(UserRecord * users)
{
	if(users != NULL && directManager.IsValid())
	{
		directManager->MemFree(users);
	}
}

void MT4NativeWrapper::UpdateTimeZone()
{
	ConCommon cfg ={0};
	directManager->CfgRequestCommon(&cfg);
	this->timeZone = cfg.timezone;
}

int MT4NativeWrapper::GetTimeZone()
{
	return timeZone;
}

MarginLevel * MT4NativeWrapper::GetUserMarginLevel(int login)
{
	if(directManager.IsValid())
	{
		if(directManager->IsConnected())
		{
			MarginLevel * ml = NULL;
			if(directManager->MarginLevelRequest(login,ml)){
				return ml;
			}
		}
	}
	return NULL;
}

time_t MT4NativeWrapper::GetServerTime()
{
	return directManager->ServerTime();
}

