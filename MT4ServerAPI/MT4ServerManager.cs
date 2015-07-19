using log4net;
using log4net.Appender;
using log4net.Repository.Hierarchy;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MT4ServerAPI
{
    public enum OrderSide
    {
        BUY,
        SELL,
        UNKNOWN
    };

    public class OrderInfo
    {
        public int Ticket;
        public int Login;
        public DateTime Time;
        public string Symbol;
        public OrderSide Side;
        public int Volume;
        public double Price;
        public int NewTicket; //for partial close
        public string Comment;
        public string Group;
    };

    public class UserDetails
    {
        public int Login;
        public string Group;
        public int Leverage;
        public double Balance;
        public double Credit;
        public string Name;
    };

    public class MT4ServerManager
    {
        public delegate void OnMsgDelegate(string msg, int type);
        public delegate void OnOpenTradeDelegate(OrderInfo orderInfo);
        public delegate void OnCloseTradeDelegate(OrderInfo orderInfo);
        public delegate void OnConnectionChangedDelegate(int connType, bool bConnected);

        public event OnMsgDelegate OnNewMsg;
        public event OnOpenTradeDelegate OnOpenTrade;
        public event OnCloseTradeDelegate OnCloseTrade;
        public event OnConnectionChangedDelegate OnConnectionChanged;

        public static readonly ILog log = LogManager.GetLogger(typeof(MT4ServerManager));
        public MT4ServerManager()
        {
            socket.OnLogin += socket_OnLogin;
            socket.OnOpenOrder += socket_OnOpenOrder;
            socket.OnCloseOrder += socket_OnCloseOrder;
            socket.OnGetTrades += socket_OnGetTrades;
            socket.OnTradeAdd += socket_OnTradeAdd;
            socket.OnTradesAdd += socket_OnTradesAdd;
            socket.OnMsg += socket_OnMsg;
            InitLogger();
        }

        void InitLogger()
        {
            string msg = "";
            string config_file_name = Directory.GetCurrentDirectory() + @"\log4net.config";
            if (!File.Exists(config_file_name))
            {
                CreateDefLoggerConfigFile(config_file_name);
            }
            FileInfo file = new FileInfo(config_file_name);
            log4net.Config.XmlConfigurator.Configure(file);
            if (!log4net.LogManager.GetRepository().Configured)
            {
                msg = string.Format("Fail to configure log4net logger from file {0}", config_file_name);
                //OnNewMsg(msg, (int)eMsgType.ERR);
                throw new Exception(msg);
            }
            var rootAppender = ((Hierarchy)LogManager.GetRepository())
                                                     .Root.Appenders.OfType<FileAppender>()
                                                     .FirstOrDefault();
            string filename = rootAppender != null ? rootAppender.File : string.Empty;
            msg = string.Format("Init logger {0}", filename);
            //OnNewMsg(msg, (int)eMsgType.INFO);
        }

        void CreateDefLoggerConfigFile(string fileName)
        {
            StreamWriter sw = new StreamWriter(fileName);
            StringBuilder sb = new StringBuilder("<log4net>");
            sb.AppendLine("<appender name=\"myFileAppender\" type=\"log4net.Appender.FileAppender\">");
            sb.AppendLine("<file value=\"MT4ServerManager.log\" />");
            sb.AppendLine("<lockingModel type=\"log4net.Appender.FileAppender+MinimalLock\" />");
            sb.AppendLine("<appendToFile value=\"true\" />");
            sb.AppendLine("<rollingStyle value=\"Date\" />");
            sb.AppendLine("<maximumFileSize value=\"5MB\" />");
            sb.AppendLine("<layout type=\"log4net.Layout.PatternLayout\">");
            sb.AppendLine("<conversionPattern value=\"%date [%thread] %-5level %message%newline\" />");
            sb.AppendLine("</layout>");
            sb.AppendLine("<filter type=\"log4net.Filter.LevelRangeFilter\">");
            sb.AppendLine("<levelMin value=\"INFO\" />");
            sb.AppendLine("<levelMax value=\"FATAL\" />");
            sb.AppendLine("</filter>");
            sb.AppendLine("</appender>");
            sb.AppendLine("<root>");
            sb.AppendLine("<priority value=\"Info\"/>");
            sb.AppendLine("<appender-ref ref=\"myFileAppender\" />");
            sb.AppendLine("</root>");
            sb.AppendLine("<logger name=\"myFileAppender\">");
            sb.AppendLine("<level value=\"Info\" />");
            sb.AppendLine("<appender-ref ref=\"myFileAppender\" />");
            sb.AppendLine("</logger>");
            sb.AppendLine("</log4net>");
            sw.WriteLine(sb.ToString());
            sw.Close();
        }

        void socket_OnTradesAdd(List<STradesAdd> Obj)
        {
            
        }

        void socket_OnMsg(string Obj, eMsgType Type)
        {
            OnNewMsg(Obj, (int)Type);
        }

        void socket_OnTradeAdd(STradesAdd Obj)
        {
            if (Obj.OrderInfo.IsOpenOrder())
            {
                log.InfoFormat("OnOpenTrade event sent for {0} {1} {2}", Obj.OrderInfo.Login, Obj.OrderInfo.Ticket, Obj.OrderInfo.NewTicket);
                OnOpenTrade(Obj.OrderInfo.ToOrderInfo());
            }
            else if (Obj.OrderInfo.IsCloseOrder())
            {
                log.InfoFormat("OnCloseTrade event sent for {0} {1} {2}", Obj.OrderInfo.Login, Obj.OrderInfo.Ticket, Obj.OrderInfo.NewTicket);
                OnCloseTrade(Obj.OrderInfo.ToOrderInfo());
            }
            else
            {
                log.InfoFormat("OnTradeAdd event NOT sent for {0} {1} {2}", Obj.OrderInfo.Login, Obj.OrderInfo.Ticket, Obj.OrderInfo.NewTicket);
            }
        }

        void socket_OnGetTrades(SGetTradesReturn Obj)
        {
            
        }

        void socket_OnCloseOrder(SOrderReturnCode Obj)
        {
            
        }

        void socket_OnOpenOrder(SOrderReturnCode Obj)
        {
            
        }

        void socket_OnLogin(SReturnCode returnCode)
        {
        }

        public bool Connect(string serverAddress, int login, string password)
        {
            int ix = serverAddress.IndexOf(':');
            string ip = "";
            int port = 4444;
            if (ix >= 0)
            {
                ip = serverAddress.Substring(0, ix).Trim();
                port = Convert.ToInt32(serverAddress.Substring(ix + 1, serverAddress.Length - ix - 1).Trim());
            }
            bool ret = socket.Connect(ip, port);
            if (ret)
            {
                socket.SendLogin(login, password);
                if (!socket.LoggedIn)
                {
                    ret = false;
                    Disconnect();
                    OnConnectionChanged(0, false);
                    OnConnectionChanged(1, false);
                }
                else 
                {
                    OnConnectionChanged(0, true);
                    OnConnectionChanged(1, true);
                }
            }
            return ret;
        }
        public bool Disconnect()
        {
            bool ret = socket.Disconnect();
            if (ret)
            {
                OnConnectionChanged(0, false);
                OnConnectionChanged(1, false);
            }
            return ret;
        }
        public int OpenOrder(int login, OrderSide side, string symbol, int volume, double openPrice, string comment, ref string errorMsg, ref DateTime openTime)
        {
            return socket.SendOpenOrder(login, (eOrderSide)side, symbol, volume, openPrice, comment, ref errorMsg, ref openTime);
        }
        public int CloseOrder(int ticket, OrderSide side, string symbol, int volume, double closePrice, string comment, bool bPartial, ref string errorMsg, ref DateTime closeTime)
        {
            return socket.SendCloseOrder(ticket, (eOrderSide)side, symbol, volume, closePrice, comment, bPartial, ref errorMsg, ref closeTime);
        }

        //Callback function called from the native level
        public void OnLog(string msg) 
        {
        }
        public void OnError(string msg)
        {

        }
        public void OnOrderOpened(OrderInfo trade, int type)
        {

        }
        public void OnOrderClosed(OrderInfo trade, int type)
        {

        }
        public void OnNativeConnectionChanged(int connType, bool bConnected)
        {

        }

        public bool CreateUser(int login, string name, string psw, string group, int deposit)
        {
            return socket.SendCreateUser(login, name, psw, group, deposit);
        }
        public string GetUserName(int login)
        {
            SGetUserInfo ret = socket.SendGetUserInfo(login);
            if (ret != null && ret.ReturnCode == eReturnCode.E_Success)
                return ret.Name;
            else
                return string.Empty;
        }
        public UserDetails GetUser(int login)
        {
            SGetUserInfo ret = socket.SendGetUserInfo(login);
            if (ret != null && ret.ReturnCode == eReturnCode.E_Success)
                return ret.ToUserDetails();
            else
                return null;
        }
        public bool GetTrades(string Groups, List<int> logins, DateTime sinceTime, ref Dictionary<int, List<OrderInfo>> openDic, ref Dictionary<int, List<OrderInfo>> closeDic)
        {
            log.InfoFormat("Start GetTrades (Groups={0}, CountLogins={1})", Groups, logins.Count);
            bool ret = true;
            int login = -1;
            List<OrderInfo> lst_oi = null;
            List<STradesAdd> list_open = null;
            List<STradesAdd> list_close = null;
            string[] _groups = Groups.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var group in _groups)
            {
                lst_oi = new List<OrderInfo>(); // Allocate anyway because that is what orig code expects
                list_open = socket.SendGetTrades(0, group, sinceTime, DateTime.Now, eGetOrderType.E_OpenOrders);
                if (list_open != null && list_open.Count > 0)
                {
                    foreach (var order in list_open)
                    {
                        if (order.OrderInfo.Login != login)
                        {
                            if (login > -1)
                            {
                                openDic.Add(login, lst_oi);
                            }
                            login = order.OrderInfo.Login;
                            lst_oi = new List<OrderInfo>();
                        }
                        lst_oi.Add(order.OrderInfo.ToOrderInfo());
                    }
                    // Add last item
                    openDic.Add(list_open[list_open.Count - 1].OrderInfo.Login, lst_oi);
                }

                login = -1;
                lst_oi = new List<OrderInfo>(); // Allocate anyway because that is what orig code expects
                list_close = socket.SendGetTrades(0, group, sinceTime, DateTime.Now, eGetOrderType.E_CloseOrders);
                if (list_close != null && list_close.Count > 0)
                {
                    foreach (var order in list_close)
                    {
                        if (order.OrderInfo.Login != login)
                        {
                            if (login > -1)
                            {
                                closeDic.Add(login, lst_oi);
                            }
                            login = order.OrderInfo.Login;
                            lst_oi = new List<OrderInfo>();
                        }
                        lst_oi.Add(order.OrderInfo.ToOrderInfo());
                    }
                    // Add last item
                    closeDic.Add(list_close[list_close.Count - 1].OrderInfo.Login, lst_oi);
                }
            }

            foreach (var _login in logins)
            {
                if (! openDic.ContainsKey(_login)) // Check if _login already exists
                {
                    lst_oi = new List<OrderInfo>(); // Allocate anyway because that is what orig code expects
                    list_open = socket.SendGetTrades(_login, string.Empty, sinceTime, DateTime.Now, eGetOrderType.E_OpenOrders);
                    if (list_open != null)
                    {
                        foreach (var order in list_open)
                        {
                            lst_oi.Add(order.OrderInfo.ToOrderInfo());
                        }
                    }
                    openDic.Add(_login, lst_oi);
                }

                if (!closeDic.ContainsKey(_login)) // Check if _login already exists
                {
                    lst_oi = new List<OrderInfo>(); // Allocate anyway because that is what orig code expects
                    list_close = socket.SendGetTrades(_login, string.Empty, sinceTime, DateTime.Now, eGetOrderType.E_CloseOrders);
                    if (list_close != null)
                    {
                        foreach (var order in list_close)
                        {
                            lst_oi.Add(order.OrderInfo.ToOrderInfo());
                        }
                    }
                    closeDic.Add(_login, lst_oi);
                }
            }
            log.InfoFormat("End GetTrades (Groups={0}, CountLogins={1})", Groups, logins.Count);
            return ret;
        }
        public bool GetOpenTrades(List<int> logins, ref Dictionary<int, List<OrderInfo>> openDic)
        {
            bool ret = true;
            List<OrderInfo> lst_oi = null;
            List<STradesAdd> list_open = null;
            foreach (var login in logins)
            {
                lst_oi = new List<OrderInfo>(); // Allocate anyway because that is what orig code expects
                list_open = socket.SendGetTrades(login, string.Empty, DateTime.Now, DateTime.Now, eGetOrderType.E_OpenOrders);
                if (list_open != null)
                {
                    foreach (var order in list_open)
                    {
                        lst_oi.Add(order.OrderInfo.ToOrderInfo());
                    }
                }
                openDic.Add(login, lst_oi);
            }
            return ret;
        }

        public bool GetOpenTrades(string Group, ref Dictionary<int, List<OrderInfo>> openDic)
        {
            bool ret = true;
            int login = -1;
            List<OrderInfo> lst_oi = null;
            List<STradesAdd> list_open = null;
            list_open = socket.SendGetTrades(0, Group, DateTime.Now, DateTime.Now, eGetOrderType.E_OpenOrders);
            if (list_open != null && list_open.Count > 0)
            {
                foreach (var order in list_open)
                {
                    if (order.OrderInfo.Login != login)
                    {
                        if (login > -1)
                        {
                            openDic.Add(login, lst_oi);
                        }
                        login = order.OrderInfo.Login;
                        lst_oi = new List<OrderInfo>(); 
                    }
                    lst_oi.Add(order.OrderInfo.ToOrderInfo());
                }
                // Add last item
                openDic.Add(list_open[list_open.Count - 1].OrderInfo.Login, lst_oi);
            }
            return ret;
        }

        public DateTime GetServerTime()
        {
            return socket.SendGetServerTime();
        }

        // Members
        MySocket socket = new MySocket();
    }
}
