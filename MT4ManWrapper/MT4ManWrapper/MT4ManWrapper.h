// MT4ManWrapper.h
#pragma once

using namespace System;
using namespace System::Collections::Generic;

#include "MT4ManagerAPI.h"
#include <set>
#include <string>

#using <system.dll>

using namespace std;
using namespace System::Timers;
using namespace System::Threading;

namespace MT4
{
	/////////////////
	//Enums
	/////////////////
	public enum class OrderSide
	{
		BUY,
		SELL,
		UNKNOWN
	};

	public ref class OrderInfo
    {
    public:
		property int Ticket;
		property int Login;
        property String ^ Group ;
		property DateTime ^ Time;
        property String ^ Symbol ;
		property OrderSide ^ Side;
        property int Volume;
        property double Price;
		property int NewTicket; //for partial close
		property String ^ Comment;
    };
    
    public ref class UserDetails
    {
    public:
        property int Login;
        property String ^ Group ;
        property int Leverage;
        property double Balance;
        property double Credit;
        property String ^ Name ;
        property double Equity;
    };

    ref class MT4Manager; //forward declaration

	/////////////////////////////////////////
    // Native MT4 Manager helper class
	/////////////////////////////////////////
    class CManager
    {
    private:
       CManagerFactory    m_factory;
       CManagerInterface * m_manager;
    public:
       CManager() : m_factory("c:\\mtmanapi.dll"),m_manager(NULL)
       {    
		  m_manager = NULL;
          m_factory.WinsockStartup();
          if(m_factory.IsValid()==FALSE || (m_manager=m_factory.Create(ManAPIVersion))==NULL)
          {
            //throw("Failed to create manager interface");
            return;
          }
       }
	   ~CManager()
	   {
		  if(m_manager!=NULL)
		  {
			 if(m_manager->IsConnected()!=FALSE) m_manager->Disconnect();
			 m_manager->Release();
			 m_manager=NULL;
		  }
		  m_factory.WinsockCleanup();
	   }
	   bool IsValid(){ return(m_manager!=NULL);}
	   CManagerInterface* operator->() { return(m_manager);}
	   bool Manager_ReCreate()
	   {
		  if(m_manager!=NULL)
		  {
			 if(m_manager->IsConnected()!=FALSE) m_manager->Disconnect();
			 m_manager->Release();
			 m_manager=NULL;
		  }
		  //m_factory.WinsockCleanup();
		  //m_factory.WinsockStartup();
		  if(m_factory.IsValid()==FALSE || (m_manager=m_factory.Create(ManAPIVersion))==NULL)
          {
            //throw("Failed to create manager interface");
            return false;
          }
		  return true;
	   }
};

	//////////////////////////////////////
	/// Native MT4 Wrapper
	//////////////////////////////////////
    class MT4NativeWrapper 
    {
        protected:
			char * serverAddress;
			int login;
			char * psw;
            CManager pumpManager; //used for getting trade events from the mt4 server
			CManager directManager; //used for openning/closing trades on the mt4 server
			bool bShouldBeConnected;//indicates if the system received a connect command and should be connected
			bool bDirectManagerConnected;
			bool bPumpManagerConnected;
            gcroot<MT4Manager ^> m_ManagedWrapper; //is the managed(c++/cli) wrapper
            int timeZone; // the server timezone

			void UpdateTimeZone();
			int GetTimeZone();
			bool InnerConnect();
			bool InnerDisconnect();
			bool ConnectPump();
			bool ConnectDirect();
			bool DisconnectPump();
			bool DisconnectDirect();
			

        public:
            inline MT4NativeWrapper(MT4Manager ^ managedWrapper)
            {
                m_ManagedWrapper = managedWrapper;
				bShouldBeConnected = false;
				bDirectManagerConnected = false;
				bPumpManagerConnected = false;
				serverAddress = NULL;
				psw = NULL;
            }
		
            bool Connect(char * _serverAddress,int _login,char * _psw); //connect to the mt4 server
			bool Disconnect(); //disconnect from the mt4 server (currently not functioning)
			bool IsConnected();
			time_t GetServerTime();
			
			int OpenOrder(int login,int cmd,char * symbol,int volume,double openPrice,char * comment,char * errMsg,long * openTime);
			bool CloseOrder(int ticket,int cmd,char * symbol,int volume,double closePrice,char * comment,char * errMsg,bool bIsPartial,long * closeTime);

            void OnLog(char * msg); //send logs
			void OnError(char * msg); //send errors
            void OnTrade(TradeRecord * trade,int type); //when a trade order is received
            void OnPumpConnectionChanged(bool bConnected); //when the pump connection has changed
			void OnDirectConnectionChanged(bool bConnected); //when the direct connection has changed
            
			bool CreateAccount(int login,char * name,char * psw,char * group,int deposit);
			char * GetUserName(int login);
            UserRecord * GetUser(int login);
            int GetAllUsers(char * group,UserRecord ** users);
			int GetOpenTrades(TradeRecord ** trades);
			int GetOpenTradesByGroup(char * group,TradeRecord ** trades);
			int GetClosedTrades(TradeRecord ** trades,int login,time_t from,time_t to);
			void FreeTradesMem(TradeRecord * trades);
			void FreeUserMem(UserRecord * users);
			void KeepAlive();
			MarginLevel * GetUserMarginLevel(int login);
			
    };

	//////////////////////////////////////////
    //Managed MT4 manager Wrapper class
	//////////////////////////////////////////
	public ref class MT4Manager
	{
        private:
			bool isDisposed;
            MT4NativeWrapper * nativeWrapper;
			int timeZone;
			bool startedKeepAlive;
			static Object ^ keepAliveLocker = gcnew Object(); //used for keep alive thread sync
			System::Timers::Timer ^ keepAliveTimer; // the keep alive thread
			void KeepAliveTimer_Tick( Object^ source, ElapsedEventArgs^ e )
		    {
				KeepAlive();
				//if(!Monitor::TryEnter(keepAliveLocker))
				//	return;
				//nativeWrapper->KeepAlive();
				//Monitor::Exit(keepAliveLocker);
			}
		protected:
			// Finalize
			!MT4Manager(){CleanUp(false);}
			void CleanUp(bool disposing);
			DateTime ^ ParseTime(time_t tt);
			OrderInfo ^ ParseOpenTrade(TradeRecord * trade);
			OrderInfo ^ ParseClosedTrade(TradeRecord * trade);
			
			void SendError(String ^ msg);
			void SendMsg(String ^ msg);
			
        public:
			//published Events
            delegate void OnMsgDelegate(String ^ msg,int type);
            delegate void OnOpenTradeDelegate(OrderInfo ^orderInfo);
			delegate void OnCloseTradeDelegate(OrderInfo ^orderInfo);
            delegate void OnConnectionChangedDelegate(int connType,bool bConnected);

            event OnMsgDelegate ^ OnNewMsg;
            event OnOpenTradeDelegate ^ OnOpenTrade;
			event OnCloseTradeDelegate ^ OnCloseTrade;
            event OnConnectionChangedDelegate ^ OnConnectionChanged;

			//Constructor / Destructor
            MT4Manager();
			~MT4Manager(){ CleanUp(true); }
			
			//Callback function called from the native level
			void OnLog(char * msg);
			void OnError(char * msg);
            void OnOrderOpened(TradeRecord * trade,int type);
			void OnOrderClosed(TradeRecord * trade,int type);
            void OnNativeConnectionChanged(int connType,bool bConnected); 
			DateTime ^ GetServerTime();
			
			//public function
			void KeepAlive()
		    {
				/*
				if(!Monitor::TryEnter(keepAliveLocker))
				{
					SendMsg("KeepAlive Can't Lock");
					return;
				}
				SendMsg("Start KeepAlive");
				try{
					if(nativeWrapper != NULL)
						nativeWrapper->KeepAlive();
				}
				catch(Exception ^ ex)
				{
					SendError(ex->Message);
				}
				SendMsg("End KeepAlive");
				Monitor::Exit(keepAliveLocker);
				*/
				if(startedKeepAlive){
					return;
				}
				startedKeepAlive = true;
				//SendMsg("Start KeepAlive");
				try{
					if(nativeWrapper != NULL)
						nativeWrapper->KeepAlive();
				}
				catch(Exception ^ ex)
				{
					SendError(ex->Message);
				}
				//SendMsg("End KeepAlive");
				startedKeepAlive = false;
			}
			bool IsConnected(){ return (nativeWrapper->IsConnected()); }

			bool Connect(String ^ serverAddress,int login,String ^ password);
			bool Disconnect();
			int  OpenOrder(int login,OrderSide side,String ^ symbol,int volume,double openPrice,String ^ comment,String ^ %errorMsg,DateTime %openTime);
			int CloseOrder(int ticket,OrderSide side,String ^ symbol,int volume,double closePrice,String ^ comment,bool bPartial,String ^ %errorMsg,DateTime %closeTime);
			
			bool CreateUser(int login,String ^ name,String ^ psw,String ^ group,int deposit);
			String ^ GetUserName(int login);
            UserDetails ^ GetUser(int login);
			bool GetTrades(String ^ groups,List<int>^ logins,DateTime^ sinceTime,Dictionary<int,List<OrderInfo^>^>^ %openDic,Dictionary<int,List<OrderInfo^>^>^ %closeDic);
            bool GetOpenTrades(List<int>^ logins,Dictionary<int,List<OrderInfo^>^>^ %openDic);
			bool GetOpenTrades(String ^ groups,Dictionary<int,List<OrderInfo^>^>^ %openDic);
	};
}


