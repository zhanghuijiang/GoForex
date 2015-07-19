using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.Threading;
using System.Globalization;
using System.Text.RegularExpressions;
//using MT4;
using MT4ServerAPI;
using System.Timers;
using Timer = System.Timers.Timer;
using System.Net.Mail;
using System.Net.Mime;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Collections.Concurrent;
using Twilio;


namespace MAM
{
    /// <summary>
    /// The main MAM class.
    /// </summary>
    public class Processor
    {
        public bool ExtendedLog { get; set; }                           // indicates whether extended log should be written and displayed
        
        //private const string _LogFile = "MAM.log";                    
        private string _LogFilePath;                                    // path to the logs files    
        private int _TimeZoneOffset;                                    // time zone offset from UTC (in hours)    
        //private MT4Manager _MTManager;                                  // interface to the MT server for trade events and executing trades
        private MT4ServerManager _MTManager;                                  // interface to the MT server for trade events and executing trades
        private bool _DirectConnected;                                  // indicates state of the direct mode connection to MT
        private bool _PumpingConnected;                                 // indicates state of the pumping mode connection to MT
        private DBAccess _DBAccess;                                     // DB interface wrapper
        private DBAccessMT4 _DBAccessMT4;                               // DB interface wrapper
        private ConcurrentDictionary<int, Manager> _Managers;           // by manager login
        private ConcurrentDictionary<string, Group> _Groups;            // by group name
        private PositionAdmin _PositionAdmin;                           // global position manager

        private Queue<TradeData> _TradeQueue;                           // queue of trades waiting for processing
        private Thread _TradeQueueProcessingThread;                     // this thread processes trades from the trade queue
        private AutoResetEvent _NewTradeEvent;                          // the event is set when a new trade registered        
        private bool _ProgramBeingClosed;                               // indicates whether the program is being closed
        private bool _OnlineTradeProcessing;                            // indicates whether incoming trades should be processed (alternatively, they are appended to the queue)

        private Timer _TimeRecordingTimer;                              // intended for time recording (if all the connections are online)
        private const int _TimeRecordingInterval = 1;                   // time recording interval (in minutes)
        private const string _TimeFile = "Time.dat";
        private int _TradesSyncInterval = 5 * 60;                       // interval of run the trades sync (in seconds)

        private HashSet<int> _TicketsToIgnore;                          // already processed manager/groups tickets (should be ignored if their OPEN orders arrive again after reconnection)
        private const int _SafetyTimeForManagerTicketsToIgnore = 20;    // time interval (in minutes) that indicates how much time we should go backward when _ManagerTicketsToIgnore is being created
        private DateTime _CreatingTimeOfManagerTicketsToIgnore;         // time when _ManagerTicketsToIgnore was created
        private const int _TimeToKeepManagerTicketsToIgnore = 20;       // time interval (in minutes) to keep _ManagerTicketsToIgnore
        private DateTime _LastTradesSyncStart;                          // time of last trades sync
        private int _MaxAttempts = 5;                                   // interval of run the trades sync (in minutes)

        private DateTime _LastAcceptedTradesSyncStart;                  // time of last Accepted trades sync
        private int _AcceptedTradesSyncInterval = 20;                   // interval of run the trades sync (in seconds)

        private static readonly object _StateAndDBLocker = new object();// intended for the internal state and DB state synchronization
        private static readonly object _TradeQueueLocker = new object();// intended for the trade queue synchronization   
        private static readonly object _LogFileLocker = new object();   // intended for the log file synchronization   
        private static readonly object _TimeFileLocker = new object();  // intended for the time file synchronization   

        public event Action<DateTime, string, string, MsgType> OnLog;   // parameters: time, source, message, message type
        public event Action<ConnType, bool> OnConnectionChanged;        // parameters: connection type (direct/pumping), connected (true/false)

        private Dictionary<int, List<int>> SymbolTransform_Manager_Accounts;    // manager accounts that his clients need symbol transformation by transform type
        private Dictionary<int, List<int>> SymbolTransform_Client_Accounts;     // client accounts that need symbol transformation by transform type

        private Dictionary<int, List<int>> VolumeTransform_Client_Accounts;     // client accounts that need volume transformation by transform type

        public int DelayOpenTrades { get; set; }
        public int DelayOpenTradesIncrement { get; set; }
        public int DelayCloseTrades { get; set; }

        private static readonly Regex mamComment = new Regex(@"MAM Open (\d+).*#(\d+)");
        private static readonly Regex LeadingInteger = new Regex(@"(\d+)");
        private static string[] phoneNumbers = null;

        private bool TryParseMamComment(string comment, out int login, out int ticket)
        {
            login = 0;
            ticket = 0;
            Match match = mamComment.Match(comment);
            if (!match.Success) { return false; }
            if (match.Groups.Count != 3 || !match.Groups[1].Success || !match.Groups[2].Success) { return false; }
            return int.TryParse(match.Groups[1].Value, out login) && int.TryParse(match.Groups[2].Value, out ticket);
        }


        public bool IsDirectConnected {
            get {
                return _DirectConnected;
            }
        }

        public Processor(int timeZoneOffset, string logFilePath)
        {
            _TimeZoneOffset = timeZoneOffset;
            _LogFilePath = logFilePath;

            SymbolTransform_Manager_Accounts = new Dictionary<int,List<int>>();
            SymbolTransform_Client_Accounts = new Dictionary<int, List<int>>();

            VolumeTransform_Client_Accounts = new Dictionary<int, List<int>>();

            DelayOpenTrades = 200;
            DelayOpenTradesIncrement = 5;
            DelayCloseTrades = 100;
        }

        public void Initialize(string dbConnectionString, string dbConnectionString_MT4)
        {
            try
            {
                ExtendedLog = true;

                _TicketsToIgnore = null;
                _CreatingTimeOfManagerTicketsToIgnore = Constants.DEFAULT_TIME;

                _DBAccess = new DBAccess(dbConnectionString);
                _DBAccessMT4 = new DBAccessMT4(dbConnectionString_MT4);

                ReconstructStateFromDB();

                _DirectConnected = false;
                _PumpingConnected = false;
                _ProgramBeingClosed = false;
                _OnlineTradeProcessing = false;

                _TradeQueue = new Queue<TradeData>();
                _NewTradeEvent = new AutoResetEvent(false);

                // start the time recording timer
                _TimeRecordingTimer = new Timer(_TimeRecordingInterval * 60 * 1000);
                _TimeRecordingTimer.AutoReset = true;
                _TimeRecordingTimer.Elapsed += RecordTimeIfOnline;
                _TimeRecordingTimer.Start();

                // create thread for processing the trade queue
                _TradeQueueProcessingThread = new Thread(ProcessTradeQueue);
                _TradeQueueProcessingThread.IsBackground = true;
                _TradeQueueProcessingThread.Start();

                //_MTManager = new MT4Manager();
                _MTManager = new MT4ServerManager();

                _MTManager.OnNewMsg += MTManager_OnNewMsg;
                _MTManager.OnConnectionChanged += MTManager_OnConnectionChanged;
                _MTManager.OnOpenTrade += MTManager_OnOpenTrade;
                _MTManager.OnCloseTrade += MTManager_OnCloseTrade;
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
            }  
            catch (Exception e)
            {
                SendAndWriteLog("Processor.Initialize method", e.Message, MsgType.ERROR, e);
            }
        }

        public void Deinit()
        {
            _ProgramBeingClosed = true;
            _TradeQueueProcessingThread.Join();
            _TimeRecordingTimer.Stop();
            _TimeRecordingTimer.Dispose();
            // TO BE DONE: Check if MTManager must be disposed
        }

        /// <summary>
        /// Connects to the MT manager
        /// </summary>
        /// <param name="serverAddress"></param>
        /// <param name="login"></param>
        /// <param name="password"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool Connect(string serverAddress, int login, string password)
        {
            try
            {
                var res = _MTManager.Connect(serverAddress, login, password);
                if(res){
                    var t = (DateTime)_MTManager.GetServerTime();
                    var a = (DateTime.UtcNow - t).TotalHours;
                    var b = Math.Abs(Math.Round(a));
                    _TimeZoneOffset = (int)b;
                }
                return res;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.Connect method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the MT manager
        /// </summary>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool Disconnect()
        {
            try
            {
                return _MTManager.Disconnect();
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.Disconnect method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }

        /// <summary>
        /// This method is invoked when status of the direct/pumping mode connection has changed 
        /// </summary>
        /// <param name="connType"></param>
        /// <param name="connected"></param>
        private void MTManager_OnConnectionChanged(int connType, bool connected)
        {
            try
            {
                if ((ConnType)connType == ConnType.DIRECT)
                {
                    if (_DirectConnected != connected)
                    {
                        if (connected)
                            SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", "Direct connected", MsgType.INFO, null);
                        else
                            SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", "Direct lost connection", MsgType.WARNING, null);

                    }
                    _DirectConnected = connected;
                }
                else if ((ConnType)connType == ConnType.PUMP)
                {
                    if (_PumpingConnected != connected)
                    {
                        if (connected)
                            SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", "Pump connected", MsgType.INFO, null);
                        else
                            SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", "Pump lost connection", MsgType.WARNING, null);

                    }
                    _PumpingConnected = connected;
                }
                else
                {
                    SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", "Invalid connection type.", MsgType.ERROR, null);
                    return;
                }

                if (OnConnectionChanged != null)
                {
                    OnConnectionChanged.BeginInvoke((ConnType)connType, connected, null, null);
                }

                if (!_DirectConnected || !_PumpingConnected)
                {
                    // disable online trade processing
                    _OnlineTradeProcessing = false;
                    //_MTManager.KeepAlive();
                }
                else if (!_OnlineTradeProcessing && _DirectConnected && _PumpingConnected)
                {
                    SendSMSToDev("MAM. The connection has been established.");
                    //SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", "Start ProcessTradesOffline thread. OnlineTradeProcessing: " + _OnlineTradeProcessing, MsgType.INFO, null);
                    // Connection (both the direct and the pumping modes) has been established (or re-established).
                    // Process trades offline (among them can be already processed trades, however 
                    // the system is capable to recognize them and not to process them again). 
                    Thread offlineTradeProcessingThread = new Thread(ProcessTradesOffline);
                    offlineTradeProcessingThread.IsBackground = true;
                    offlineTradeProcessingThread.Start();
                    offlineTradeProcessingThread.Join(); // wait until completion 

                    //SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", "End ProcessTradesOffline thread.", MsgType.INFO, null);

                    // enable online trade processing
                    _OnlineTradeProcessing = true;

                    // if during offline trade processing new trades have arrived, 
                    // send signal to the trade processing thread 
                    if (_TradeQueue.Count > 0)
                    {
                        _NewTradeEvent.Set();
                    }
                }
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.MTManager_OnConnectionChanged method", e.Message, MsgType.ERROR, e);
            }
        }

        /// <summary>
        /// Any message received from the MT manager should be forwarded to the application main class.
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="msgType"></param>
        private void MTManager_OnNewMsg(string msg, int msgType)
        {
            SendAndWriteLog("MTManager", msg, (MsgType)msgType, null);
            if (msgType == (int)MsgType.ERROR)
            {
                SendSMSToDev("MAM Error: " + msg);
            }
        }

        /// <summary>
        /// process trades online
        /// This method waits for a new entry in the trade queue and processes it.
        /// </summary>
        private void ProcessTradeQueue()
        {
            // this list is intended for the trade queue synchronization
            List<TradeData> tradesToProcess = new List<TradeData>();
            _LastTradesSyncStart = DateTime.MinValue;

            while (!_ProgramBeingClosed)
            {
                try
                {
                    // Wait 100 milliseconds for a new trading event.
                    // If an event has arrived and online trade processing enabled, process it.
                    if (_NewTradeEvent.WaitOne(100) && !_ProgramBeingClosed && _OnlineTradeProcessing)
                    {
                        // for the trade queue synchronization, move all the trades to be processed into the list 
                        tradesToProcess.Clear();
                        lock (_TradeQueueLocker)
                        {
                            while (_TradeQueue.Count > 0)
                            {
                                tradesToProcess.Add(_TradeQueue.Dequeue());
                            }
                        }

                        // process the trades
                        foreach (TradeData trade in tradesToProcess)
                        {
                            try
                            {
                                //if the order is to close a position
                                if (trade.Close)
                                {
                                    ProcessCloseTrade(trade.OrderInfo, ExtendedLog, null);
                                }
                                else
                                {
                                    ProcessOpenTrade(trade.OrderInfo, ExtendedLog, null);
                                }
                            }
                            catch (DBAccess_Exception e)
                            {
                                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                            }
                            catch (Exception e)
                            {
                                SendAndWriteLog("Processor.ProcessTradeQueue method", e.Message, MsgType.ERROR, e);
                            }
                        }

                        // if _ManagerTicketsToIgnore exists more than _TimeToKeepManagerTicketsToIgnore, delete it                        
                        if (_TicketsToIgnore != null && SystemTimeNow() > _CreatingTimeOfManagerTicketsToIgnore.AddMinutes(_TimeToKeepManagerTicketsToIgnore))
                        {
                            _TicketsToIgnore = null;
                            _CreatingTimeOfManagerTicketsToIgnore = Constants.DEFAULT_TIME;
                        }

                        if (_AcceptedTradesSyncInterval > 0 && _LastAcceptedTradesSyncStart.AddSeconds(_AcceptedTradesSyncInterval) < DateTime.Now)
                        {
                            ProcessAcceptedTradesFix();
                            _LastAcceptedTradesSyncStart = DateTime.Now;
                        }

                        if (_LastTradesSyncStart.AddSeconds(_TradesSyncInterval) < DateTime.Now)
                        {
                            lock (_TradeQueueLocker)
                            {
                                if (_TradeQueue.Count == 0)
                                {
                                    int deltaType = 1;
                                    if (_LastTradesSyncStart == DateTime.MinValue || DateTime.Now.Minute < 60 * _TradesSyncInterval)
                                    {
                                        deltaType = 2;
                                    }
                                    ProcessTradesSync(deltaType);
                                    _LastTradesSyncStart = DateTime.Now;
                                }
                            }
                        }
                    }
                }
                catch (DBAccess_Exception e)
                {
                    SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                }
                catch (Exception e)
                {
                    SendAndWriteLog("Processor.ProcessTradeQueue method", e.Message, MsgType.ERROR, e);
                }
            }            
        }

        /// <summary>
        /// Catches an open trade, saves it in the trade queue and signals the trade processing thread
        /// </summary>
        /// <param name="orderInfo"></param>
        private void MTManager_OnOpenTrade(OrderInfo orderInfo)
        {
            lock (_TradeQueueLocker)
            {
                _TradeQueue.Enqueue(new TradeData(false, orderInfo));
            }            
    
            _NewTradeEvent.Set();
        }

        /// <summary>
        /// Processes an open trade
        /// </summary>
        /// <param name="orderInfo"></param>
        /// <param name="extendedLog"></param>
        private void ProcessOpenTrade(OrderInfo orderInfo, bool extendedLog, string sourceMsg)
        {
            int login = orderInfo.Login;
            string groupName = orderInfo.Group;


            bool ProcessGroup = false;
            bool ProcessManager = false;

            var time1 = _DBAccess.GetManagerLastTradeTime();
            var time2 = _DBAccess.GetClientLastTradeTime();
            var minTime = (time1 < time2 ? time1 : time2);
            if (minTime > new DateTime(2000, 1, 1))
            {
                minTime = minTime.AddMinutes(-5);
            }

            // check if the ticket doesn't exist in the system currently (otherwise, it is an order update), 
            if (_PositionAdmin.ManagerTicketExists(orderInfo.Ticket))
            {
                var msg1 = string.Format("Ticket already exist. Manager={0}, Group={1}, Ticket={2}", orderInfo.Login, orderInfo.Group, orderInfo.Ticket);
                SendAndWriteLog("Processor.ProcessOpenTrade method", msg1, MsgType.INFO, null);
                return;
            }

            if (string.IsNullOrEmpty(orderInfo.Group))
            {
                var msg1 = string.Format("Ticket group not found. Manager={0}, Group={1}, Ticket={2}", orderInfo.Login, orderInfo.Group, orderInfo.Ticket);
                SendAndWriteLog("Processor.ProcessOpenTrade method", msg1, MsgType.INFO, null);
            }
            else
            {
                // the order must belong to a group manager that has at least one client subscribed;
                // also, it is not a consequence of a partial close command, and order creation time is after last trade time
                // finally, it has not already been processed (the last condition is relevant after reconnection). 
                if (!_Groups.IsEmpty && _Groups.ContainsKey(orderInfo.Group))
                {
                    var _groupHasClients = (_Groups[groupName].Clients.Count > 0);
                    var _partialClose = orderInfo.Comment.Contains("from #");
                    var _partialCloseTicket = (_partialClose ? ParseInteger(orderInfo.Comment) : 0);
                    var _closeHedge = false;
                    var _groupTicketsToIgnore = (_TicketsToIgnore != null && _TicketsToIgnore.Contains(orderInfo.Ticket));
                    var _groupCreatedAfterTicket = _Groups[groupName].CreateDate > (DateTime)orderInfo.Time;

                    if (_partialCloseTicket > 0)
                    {
                        _closeHedge = _DBAccess.IsPartialClose(_partialCloseTicket);
                        if (_closeHedge)
                        {
                            _partialClose = false;
                        }
                    }

                    if (_groupHasClients && !_partialClose && !_groupTicketsToIgnore && !_groupCreatedAfterTicket)
                    {
                        if (!string.IsNullOrEmpty(sourceMsg))
                        {
                            SendAndWriteLog("Processor.ProcessOpenTrade method", sourceMsg, MsgType.WARNING, null);
                        }
                        if ((DateTime)orderInfo.Time < minTime)
                        {
                            var msg1 = new StringBuilder("Order creation time is before last trade time. ");
                            msg1.Append("Manager=" + orderInfo.Login + ", ");
                            msg1.Append("Group=" + orderInfo.Group + ", ");
                            msg1.Append("Ticket=" + orderInfo.Ticket + ", ");
                            msg1.Append("Symbol=" + orderInfo.Symbol + ", ");
                            msg1.Append("Side=" + orderInfo.Side + ", ");
                            msg1.Append("Volume=" + orderInfo.Volume + ", ");
                            msg1.Append("Price=" + orderInfo.Price + ", ");
                            msg1.Append("Time=" + ((DateTime)orderInfo.Time).Format() + ", ");
                            msg1.Append("LastTime=" + minTime.Format());

                            SendAndWriteLog("Processor.ProcessOpenTrade method", msg1.ToString(), MsgType.INFO, null);
                        }
                        ProcessGroup = true;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(sourceMsg))
                        {
                            SendAndWriteLog("Processor.ProcessOpenTrade method", sourceMsg, MsgType.INFO, null);
                        }
                        var msg1 = new StringBuilder("");
                        if (!_groupHasClients)
                        {
                            msg1.Append("Group don't have clients. ");
                        }
                        if (_partialClose)
                        {
                            msg1.AppendFormat("Ticket have 'from #' comment. Comment: {0}. Ticket: {1}. Close Hedge: {2}.", orderInfo.Comment, _partialCloseTicket, _closeHedge);
                        }
                        if (_groupTicketsToIgnore)
                        {
                            msg1.Append("Ticket must be ignored. ");
                        }
                        if (_groupCreatedAfterTicket)
                        {
                            msg1.AppendFormat("Old ticket of group. Group create date: {0}. Ticket time: {1}.", _Groups[groupName].CreateDate.Format(), ((DateTime)orderInfo.Time).Format());
                        }
                        msg1.Append("Manager=" + orderInfo.Login + ", ");
                        msg1.Append("Group=" + orderInfo.Group + ", ");
                        msg1.Append("Ticket=" + orderInfo.Ticket + ", ");
                        msg1.Append("Clients=" + _Groups[groupName].Clients.Count);

                        SendAndWriteLog("Processor.ProcessOpenTrade method", msg1.ToString(), MsgType.INFO, null);
                    }
                }
            }

            // the order must belong to a manager that has at least one client subscribed;
            // also, it is not a consequence of a partial close command, and order creation time is after last trade time
            // finally, it has not already been processed (the last condition is relevant after reconnection). 
            if (!_Managers.IsEmpty && _Managers.ContainsKey(login))
            {
                var _managerHasClients = (_Managers[login].Clients.Count > 0);
                var _partialClose = orderInfo.Comment.Contains("from #");
                var _partialCloseTicket = (_partialClose ? ParseInteger(orderInfo.Comment) : 0);
                var _closeHedge = false;
                var _managerTicketsToIgnore = (_TicketsToIgnore != null && _TicketsToIgnore.Contains(orderInfo.Ticket));
                var _managerCreatedAfterTicket = _Managers[login].CreateDate > (DateTime)orderInfo.Time;

                if (_partialCloseTicket > 0)
                {
                    _closeHedge = _DBAccess.IsPartialClose(_partialCloseTicket);
                    if (_closeHedge)
                    {
                        _partialClose = false;
                    }
                }

                if (_managerHasClients && !_partialClose && !_managerTicketsToIgnore && !_managerCreatedAfterTicket)
                {
                    if (!string.IsNullOrEmpty(sourceMsg))
                    {
                        SendAndWriteLog("Processor.ProcessOpenTrade method", sourceMsg, MsgType.WARNING, null);
                    }
                    if ((DateTime)orderInfo.Time < minTime)
                    {
                        var msg1 = new StringBuilder("Order creation time is before last trade time. ");
                        msg1.Append("Manager=" + orderInfo.Login + ", ");
                        msg1.Append("Ticket=" + orderInfo.Ticket + ", ");
                        msg1.Append("Symbol=" + orderInfo.Symbol + ", ");
                        msg1.Append("Side=" + orderInfo.Side + ", ");
                        msg1.Append("Volume=" + orderInfo.Volume + ", ");
                        msg1.Append("Price=" + orderInfo.Price + ", ");
                        msg1.Append("Time=" + ((DateTime)orderInfo.Time).Format() + ", ");
                        msg1.Append("LastTime=" + minTime.Format());

                        SendAndWriteLog("Processor.ProcessOpenTrade method", msg1.ToString(), MsgType.INFO, null);
                    }
                    ProcessManager = true;
                }
                else
                {
                    if (!string.IsNullOrEmpty(sourceMsg))
                    {
                        SendAndWriteLog("Processor.ProcessOpenTrade method", sourceMsg, MsgType.INFO, null);
                    }
                    var msg1 = new StringBuilder("");
                    if (!_managerHasClients)
                    {
                        msg1.Append("Manager don't have clients. ");
                    }
                    if (_partialClose)
                    {
                        msg1.AppendFormat("Ticket have 'from #' comment. Comment: {0}. Ticket: {1}. Close Hedge: {2}.", orderInfo.Comment, _partialCloseTicket, _closeHedge);
                    }
                    if (_managerTicketsToIgnore)
                    {
                        msg1.Append("Ticket must be ignored. ");
                    }
                    if (_managerCreatedAfterTicket)
                    {
                        msg1.AppendFormat("Old ticket of manager. Manager create date: {0}. Ticket time: {1}.", _Managers[login].CreateDate.Format(), ((DateTime)orderInfo.Time).Format());
                    }
                    msg1.Append("Manager=" + orderInfo.Login + ", ");
                    msg1.Append("Ticket=" + orderInfo.Ticket + ", ");
                    msg1.Append("Clients=" + _Managers[login].Clients.Count);

                    SendAndWriteLog("Processor.ProcessOpenTrade method", msg1.ToString(), MsgType.INFO, null);
                }
            }

            if (ProcessGroup)
            {
                ProcessGroupOpenTrade(orderInfo, extendedLog);
            }

            if (ProcessManager)
            {
                ProcessManagerOpenTrade(orderInfo, extendedLog);
            }
            // check if this is ACCEPTED order (client order that was in queue)
            CheckFindAcceptedTrade(orderInfo);

        }

        /// <summary>
        /// Check if this is ACCEPTED order
        /// </summary>
        /// <param name="OrderInfo"></param>
        private void CheckFindAcceptedTrade(OrderInfo orderInfo)
        {
            // try find ticket by trade comment
            int findLogin = 0;
            int findticket = 0;
            if (TryParseMamComment(orderInfo.Comment, out findLogin, out findticket))
            {
                var clientPositions = _PositionAdmin.GetClientPositions(findticket);
                if (clientPositions.ContainsKey(orderInfo.Login))
                {
                    var _position = clientPositions[orderInfo.Login];
                    if (_position.CurTicket == 0 && _position.Status == PositionStatus.OPEN_IN_PROCESS && _position.Symbol == orderInfo.Symbol &&
                        _position.Side == (OrderSide)orderInfo.Side && _position.CurVolume == orderInfo.Volume && _position.ManagerTicket < orderInfo.Ticket)
                    {
                        _PositionAdmin.UpdateAcceptedClientTrade(findticket, orderInfo.Login, orderInfo.Ticket, orderInfo.Price);
                        _DBAccess.UpdateOpenClientPosition(findticket, orderInfo.Login, orderInfo.Ticket, (int)PositionStatus.OPEN_DONE);
                        _DBAccess.UpdateClientTrade(new DBClientTradeData(orderInfo, false, 0, -1, -1, -1));
                        if (true)
                        {
                            var msg = new StringBuilder("Find client ACCEPTED order (a). ");
                            msg.Append("Login=" + orderInfo.Login + ", ");
                            msg.Append("Ticket=" + orderInfo.Ticket + ", ");
                            msg.Append("Symbol=" + orderInfo.Symbol + ", ");
                            msg.Append("Side=" + orderInfo.Side + ", ");
                            msg.Append("Volume=" + orderInfo.Volume + ", ");
                            msg.Append("Price=" + orderInfo.Price + ".");

                            SendAndWriteLog("Processor.CheckFindAcceptedTrade method", msg.ToString(), MsgType.INFO, null);
                        }
                        return;
                    }
                }
            }
            else
            {
                // try find ticket in all "in process" positions
                var managerTickets = _PositionAdmin.ManagerTicketOfAcceptedClientTrade(orderInfo);
                if (managerTickets != null && managerTickets.Count > 0)
                {
                    if (managerTickets.Count > 1)
                    {
                        var msg = new StringBuilder("Find more than one client ACCEPTED ticket. ");
                        msg.Append("Manager Tickets=" + string.Join(",", managerTickets) + ". ");
                        msg.Append("Login=" + orderInfo.Login + ", ");
                        msg.Append("Ticket=" + orderInfo.Ticket + ", ");
                        msg.Append("Symbol=" + orderInfo.Symbol + ", ");
                        msg.Append("Side=" + orderInfo.Side + ", ");
                        msg.Append("Volume=" + orderInfo.Volume + ", ");
                        msg.Append("Price=" + orderInfo.Price + ".");
                        SendAndWriteLog("Processor.CheckFindAcceptedTrade method", msg.ToString(), MsgType.ERROR, null);
                    }
                    var managerTicket = managerTickets[0];
                    _PositionAdmin.UpdateAcceptedClientTrade(managerTicket, orderInfo.Login, orderInfo.Ticket, orderInfo.Price);
                    _DBAccess.UpdateOpenClientPosition(managerTicket, orderInfo.Login, orderInfo.Ticket, (int)PositionStatus.OPEN_DONE);
                    _DBAccess.UpdateClientTrade(new DBClientTradeData(orderInfo, false, 0, -1, -1, -1));
                    if (true)
                    {
                        var msg = new StringBuilder("Find client ACCEPTED order (b). ");
                        msg.Append("Login=" + orderInfo.Login + ", ");
                        msg.Append("Ticket=" + orderInfo.Ticket + ", ");
                        msg.Append("Symbol=" + orderInfo.Symbol + ", ");
                        msg.Append("Side=" + orderInfo.Side + ", ");
                        msg.Append("Volume=" + orderInfo.Volume + ", ");
                        msg.Append("Price=" + orderInfo.Price + ".");

                        SendAndWriteLog("Processor.CheckFindAcceptedTrade method", msg.ToString(), MsgType.INFO, null);
                    }
                }
            }
        }


        /// <summary>
        /// Processes a manager open trade
        /// </summary>
        /// <param name="managerOrderInfo"></param>
        private void ProcessManagerOpenTrade(OrderInfo managerOrderInfo, bool extendedLog)
        {
            StringBuilder msg;

            if (extendedLog)
            {
                msg = new StringBuilder("Processing manager open order. ");
                msg.Append("Manager=" + managerOrderInfo.Login + ", ");
                msg.Append("Ticket=" + managerOrderInfo.Ticket + ", ");
                msg.Append("Symbol=" + managerOrderInfo.Symbol + ", ");
                msg.Append("Side=" + managerOrderInfo.Side + ", ");
                msg.Append("Volume=" + managerOrderInfo.Volume + ", ");
                msg.Append("Price=" + managerOrderInfo.Price + ".");

                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.INFO, null);
            }

            if ((OrderSide)managerOrderInfo.Side != OrderSide.BUY && (OrderSide)managerOrderInfo.Side != OrderSide.SELL)
            {
                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", "Incorrect order side.", MsgType.ERROR, null);
                return;
            }

            long managerTradeID = -1;

            Manager manager = _Managers[managerOrderInfo.Login];

            // append the manager position to the position admin and to DB
            PositionInfo managerPositionInfo = new PositionInfo(managerOrderInfo.Login,
                                                                managerOrderInfo.Group,
                                                                manager.Name,
                                                                managerOrderInfo.Ticket,
                                                                managerOrderInfo.Ticket,
                                                                managerOrderInfo.Symbol,
                                                                (OrderSide)managerOrderInfo.Side,
                                                                managerOrderInfo.Volume,
                                                                1,
                                                                managerOrderInfo.Ticket,
                                                                managerOrderInfo.Price,
                                                                string.Empty,
                                                                PositionStatus.OPEN_NEW,
                                                                0,
                                                                0,
                                                                false,
                                                                false,
                                                                0);

            if (_PositionAdmin.AppendManagerPosition(managerPositionInfo))
            {
                _DBAccess.AppendManagerPosition(managerPositionInfo);
                managerTradeID = _DBAccess.AppendManagerTrade(managerOrderInfo, false, managerOrderInfo.Ticket, managerOrderInfo.Volume);
            }
            else
            {
                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", "Current manager position alredy exist. Ticket = " + managerOrderInfo.Ticket, MsgType.INFO, null);
                managerTradeID = _DBAccess.GetManagerMaxTradeID(managerOrderInfo.Ticket);
                if (managerTradeID <= 0)
                {
                    SendAndWriteLog("Processor.ProcessManagerOpenTrade method", "Can't resolve TradeID by Ticket = " + managerOrderInfo.Ticket, MsgType.ERROR, null);
                }
            }

            if (extendedLog)
            {
                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", "ManagerTradeID=" + managerTradeID + ", Clients=" + manager.Clients.Count, MsgType.INFO, null);
            }

            if (manager.Clients.Count == 0) // no client positions     
            {
                return;
            }

            List<DBClientPositionData> clientPositionsForDB = new List<DBClientPositionData>(manager.Clients.Count);
            List<DBClientTradeData> clientTradesForDB = new List<DBClientTradeData>(manager.Clients.Count);

            // calculate trade volumes for all the clients and send the corresponding orders to the MT manager

            //first calculate the fair distribution of the clients position volumes by their account multiplier(percentage)
            List<FairItem> clientVolumes = new List<FairItem>();
            bool isDividePartial = false;
            foreach (int clientLogin in manager.Clients.Keys)
            {
                var _client = manager.GetClient(clientLogin);
                if (_client == null || _client.CreateDate > (DateTime)managerOrderInfo.Time)
                {
                    msg = new StringBuilder("Client inserted after the trade created. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("CreateDate=" + _client.CreateDate + ", ");
                    msg.Append("Time=" + ((DateTime)managerOrderInfo.Time).Format() + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.ERROR, null);
                    continue;
                }
                if (_client.Multiplier > -1 && _client.Multiplier < 0)
                {
                    isDividePartial = true;
                }
                FairItem newItem = new FairItem() { ID = _client.Login, Ratio = _client.Multiplier };
                clientVolumes.Add(newItem);
            }

            try
            {
                if (!FairDistribution.Distribute(isDividePartial, managerOrderInfo.Volume, ref clientVolumes))
                {
                    // isDivideVary - true
                    SendAndWriteLog("Processor.ProcessManagerOpenTrade method", "Not found clients with positive Equity", MsgType.ERROR, null);
                    return;
                }
            }
            catch (Exception ex)
            {
                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", "FairDistribution error. IsDividePartial: " + isDividePartial, MsgType.ERROR, null);
            }

            string newSymbolFromManager = SymbolTransform_Manager(managerOrderInfo.Login, managerOrderInfo.Symbol);

            // prepare and save client positions
            //var sleepTime = DelayOpenTrades;
            var distributionList = clientVolumes.OrderBy(k => k.ID).ToList();
            foreach (FairItem distribution in distributionList)
            {
                int clientLogin = distribution.ID;
                int clientVolume = distribution.Result;
                string symbol = managerOrderInfo.Symbol;
                Client client = manager.GetClient(clientLogin);

                string newSymbolFromClient = SymbolTransform_Client(clientLogin, managerOrderInfo.Symbol);
                var newVolume = VolumeTransform_Client(clientLogin, managerOrderInfo.Symbol, clientVolume);

                if (extendedLog)
                {
                    msg = new StringBuilder("Sending client open order. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("Multiplier=" + distribution.Ratio + ", ");
                    if (!string.IsNullOrEmpty(newSymbolFromClient))
                    {
                        msg.Append("Symbol=" + newSymbolFromClient + ", ");
                        msg.Append("PrevSymbol=" + managerOrderInfo.Symbol + ", ");
                        symbol = newSymbolFromClient;
                    }
                    else if (!string.IsNullOrEmpty(newSymbolFromManager))
                    {
                        msg.Append("Symbol=" + newSymbolFromManager + ", ");
                        msg.Append("PrevSymbol=" + managerOrderInfo.Symbol + ", ");
                        symbol = newSymbolFromManager;
                    }
                    else
                    {
                        msg.Append("Symbol=" + symbol + ", ");
                    }
                    msg.Append("Side=" + (OrderSide)managerOrderInfo.Side + ", ");
                    if (newVolume > 0 && newVolume != clientVolume)
                    {
                        msg.Append("Volume=" + newVolume + ", ");
                        msg.Append("PrevVolume=" + clientVolume + ", ");
                        clientVolume = newVolume;
                    }
                    else
                    {
                        msg.Append("Volume=" + clientVolume + ", ");
                    }
                    msg.Append("Price=" + managerOrderInfo.Price + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.INFO, null);
                }

                if (clientVolume <= 0)
                {
                    msg = new StringBuilder("Open order hasn't been excluded. ");
                    msg.Append("Error=Ignoring Position - too small to be opened. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("Multiplier=" + distribution.Ratio + ", ");
                    msg.Append("Volume=" + clientVolume + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");
                    SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.WARNING, null);
                    continue;
                }

                string comment = string.Format("MAM Open {0} #{1}", manager.Login, managerOrderInfo.Ticket);

                PositionInfo clientPositionInfo = new PositionInfo(clientLogin,
                                                                   string.Empty,
                                                                   client.Name,
                                                                   0,
                                                                   0,
                                                                   symbol,
                                                                   (OrderSide)managerOrderInfo.Side,
                                                                   clientVolume,
                                                                   distribution.Ratio,
                                                                   managerOrderInfo.Ticket,
                                                                   managerOrderInfo.Price,
                                                                   comment,
                                                                   PositionStatus.OPEN_NEW,
                                                                   0,
                                                                   0,
                                                                   false,
                                                                   false,
                                                                   0);
                _PositionAdmin.AppendClientPosition(managerOrderInfo.Ticket, clientPositionInfo);

                // prepare the client position for appending to DB
                clientPositionsForDB.Add(new DBClientPositionData(managerOrderInfo.Ticket, clientPositionInfo));

                // prepare the client trade for appending to DB
                OrderInfo clientOrderInfo = new OrderInfo();
                clientOrderInfo.Login = clientLogin;
                clientOrderInfo.Ticket = 0;
                clientOrderInfo.Time = SystemTimeNow();
                clientOrderInfo.Symbol = symbol;
                clientOrderInfo.Side = (OrderSide)managerOrderInfo.Side;
                clientOrderInfo.Volume = clientVolume;
                clientOrderInfo.Price = managerOrderInfo.Price;
                clientOrderInfo.Comment = comment;
                clientOrderInfo.NewTicket = Constants.NO_TICKET;
                clientTradesForDB.Add(new DBClientTradeData(clientOrderInfo, false, 0, clientVolume, managerTradeID, distribution.Ratio));

            }

            // append all the clients' positions and trades to DB
            _DBAccess.AppendMultiClientPositions(clientPositionsForDB);
            _DBAccess.AppendMultiClientTrades(clientTradesForDB);

            //managerOrderInfo = _PositionAdmin.GetManagerPosition(ticket);

            // start open client trades
            ProcessOpenTrade_MT4(managerOrderInfo.Ticket, managerTradeID);
        }
        
        /// <summary>
        /// Processes a manager open trade
        /// </summary>
        /// <param name="groupOrderInfo"></param>
        private void ProcessGroupOpenTrade(OrderInfo groupOrderInfo, bool extendedLog)
        {
            StringBuilder msg;

            if (extendedLog)
            {
                msg = new StringBuilder("Processing group manager open order. ");
                msg.Append("Manager=" + groupOrderInfo.Login + ", ");
                msg.Append("Group=" + groupOrderInfo.Group + ", ");
                msg.Append("Ticket=" + groupOrderInfo.Ticket + ", ");
                msg.Append("Symbol=" + groupOrderInfo.Symbol + ", ");
                msg.Append("Side=" + groupOrderInfo.Side + ", ");
                msg.Append("Volume=" + groupOrderInfo.Volume + ", ");
                msg.Append("Price=" + groupOrderInfo.Price + ".");

                SendAndWriteLog("Processor.ProcessGroupOpenTrade method", msg.ToString(), MsgType.INFO, null);
            }

            if ((OrderSide)groupOrderInfo.Side != OrderSide.BUY && (OrderSide)groupOrderInfo.Side != OrderSide.SELL)
            {
                SendAndWriteLog("Processor.ProcessGroupOpenTrade method", "Incorrect order side.", MsgType.ERROR, null);
                return;
            }

            long managerTradeID = -1;
            Group group = _Groups[groupOrderInfo.Group];

            string comment = string.Format("MAM Open {0} #{1} ({2})", groupOrderInfo.Login, groupOrderInfo.Ticket, groupOrderInfo.Group);

            // append the manager position to the position admin and to DB
            PositionInfo managerPositionInfo = new PositionInfo(groupOrderInfo.Login,
                                                                groupOrderInfo.Group,
                                                                string.Empty,
                                                                groupOrderInfo.Ticket,
                                                                groupOrderInfo.Ticket,
                                                                groupOrderInfo.Symbol,
                                                                (OrderSide)groupOrderInfo.Side,
                                                                groupOrderInfo.Volume,
                                                                1,
                                                                groupOrderInfo.Ticket,
                                                                groupOrderInfo.Price,
                                                                comment,
                                                                PositionStatus.OPEN_NEW,
                                                                0,
                                                                0,
                                                                false,
                                                                false,
                                                                0);

            if (_PositionAdmin.AppendManagerPosition(managerPositionInfo))
            {
                _DBAccess.AppendManagerPosition(managerPositionInfo);
                managerTradeID = _DBAccess.AppendManagerTrade(groupOrderInfo, false, groupOrderInfo.Ticket, groupOrderInfo.Volume);
            }
            else
            {
                SendAndWriteLog("Processor.ProcessGroupOpenTrade method", "Current manager position alredy exist. Ticket = " + groupOrderInfo.Ticket, MsgType.INFO, null);
                managerTradeID = _DBAccess.GetManagerMaxTradeID(groupOrderInfo.Ticket);
                if (managerTradeID <= 0)
                {
                    SendAndWriteLog("Processor.ProcessGroupOpenTrade method", "Can't resolve TradeID by Ticket = " + groupOrderInfo.Ticket, MsgType.ERROR, null);
                }
            }

            if (extendedLog)
            {
                SendAndWriteLog("Processor.ProcessGroupOpenTrade method", "ManagerTradeID=" + managerTradeID + ".", MsgType.INFO, null);
            }

            if (group.Clients.Count == 0) // no client positions     
            {
                return;
            }

            List<DBClientPositionData> clientPositionsForDB = new List<DBClientPositionData>(group.Clients.Count);
            List<DBClientTradeData> clientTradesForDB = new List<DBClientTradeData>(group.Clients.Count);

            // calculate trade volumes for all the clients and send the corresponding orders to the MT manager

            //first calculate the fair distribution of the clients position volumes by their account multiplier(percentage)
            List<FairItem> clientVolumes = new List<FairItem>();
            bool isDividePartial = false;
            foreach (int clientLogin in group.Clients.Keys)
            {
                var _client = group.GetClient(clientLogin);
                if (_client == null || _client.CreateDate > (DateTime)groupOrderInfo.Time)
                {
                    msg = new StringBuilder("Client inserted after the trade created. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("CreateDate=" + _client.CreateDate + ", ");
                    msg.Append("Time=" + ((DateTime)groupOrderInfo.Time).Format() + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    SendAndWriteLog("Processor.ProcessGroupOpenTrade method", msg.ToString(), MsgType.ERROR, null);
                    continue;
                }
                if (_client.Multiplier > -1 && _client.Multiplier < 0)
                {
                    isDividePartial = true;
                }
                FairItem newItem = new FairItem() { ID = _client.Login, Ratio = _client.Multiplier };
                clientVolumes.Add(newItem);
            }

            try
            {
                if (!FairDistribution.Distribute(isDividePartial, groupOrderInfo.Volume, ref clientVolumes))
                {
                    // isDivideVary - true
                    SendAndWriteLog("Processor.ProcessGroupOpenTrade method", "Not found clients with positive Equity", MsgType.ERROR, null);
                    return;
                }
            }
            catch (Exception ex)
            {
                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", "FairDistribution error. IsDividePartial: " + isDividePartial, MsgType.ERROR, null);
            }

            string newSymbolFromManager = SymbolTransform_Manager(groupOrderInfo.Login, groupOrderInfo.Symbol);

            // prepare and save client positions
            //var sleepTime = DelayOpenTrades;
            foreach (FairItem distribution in clientVolumes.OrderBy(k => k.ID).ToList())
            {
                int clientLogin = distribution.ID;
                int clientVolume = distribution.Result;
                string symbol = groupOrderInfo.Symbol;
                Client client = group.GetClient(clientLogin);

                string newSymbolFromClient = SymbolTransform_Client(clientLogin, groupOrderInfo.Symbol);
                var newVolume = VolumeTransform_Client(clientLogin, groupOrderInfo.Symbol, clientVolume);

                if (extendedLog)
                {
                    msg = new StringBuilder("Sending client open order. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("Multiplier=" + distribution.Ratio + ", ");
                    if (!string.IsNullOrEmpty(newSymbolFromClient))
                    {
                        msg.Append("Symbol=" + newSymbolFromClient + ", ");
                        msg.Append("PrevSymbol=" + groupOrderInfo.Symbol + ", ");
                        symbol = newSymbolFromClient;
                    }
                    else if (!string.IsNullOrEmpty(newSymbolFromManager))
                    {
                        msg.Append("Symbol=" + newSymbolFromManager + ", ");
                        msg.Append("PrevSymbol=" + groupOrderInfo.Symbol + ", ");
                        symbol = newSymbolFromManager;
                    }
                    else
                    {
                        msg.Append("Symbol=" + symbol + ", ");
                    }
                    msg.Append("Side=" + (OrderSide)groupOrderInfo.Side + ", ");
                    if (newVolume > 0 && newVolume != clientVolume)
                    {
                        msg.Append("Volume=" + newVolume + ", ");
                        msg.Append("PrevVolume=" + clientVolume + ", ");
                        clientVolume = newVolume;
                    }
                    else
                    {
                        msg.Append("Volume=" + clientVolume + ", ");
                    }
                    msg.Append("Price=" + groupOrderInfo.Price + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.INFO, null);
                }

                if (clientVolume <= 0)
                {
                    msg = new StringBuilder("Open order hasn't been excluded. ");
                    msg.Append("Error=Ignoring Position - too small to be opened. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("Multiplier=" + distribution.Ratio + ", ");
                    msg.Append("Volume=" + clientVolume + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");
                    SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.WARNING, null);
                    continue;
                }

                comment = string.Format("MAM Open {0} #{1} ({2})", groupOrderInfo.Login, groupOrderInfo.Ticket, group.Name);

                PositionInfo clientPositionInfo = new PositionInfo(clientLogin,
                                                                        group.Name,
                                                                        client.Name,
                                                                        0,
                                                                        0,
                                                                        symbol,
                                                                        (OrderSide)groupOrderInfo.Side,
                                                                        clientVolume,
                                                                        distribution.Ratio,
                                                                        groupOrderInfo.Ticket,
                                                                        groupOrderInfo.Price,
                                                                        comment,
                                                                        PositionStatus.OPEN_NEW,
                                                                        0,
                                                                        0,
                                                                        false,
                                                                        false,
                                                                        0);

                _PositionAdmin.AppendClientPosition(groupOrderInfo.Ticket, clientPositionInfo);

                // prepare the client position for appending to DB
                clientPositionsForDB.Add(new DBClientPositionData(groupOrderInfo.Ticket, clientPositionInfo));

                // prepare the client trade for appending to DB
                OrderInfo clientOrderInfo = new OrderInfo();
                clientOrderInfo.Login = clientLogin;
                clientOrderInfo.Group = group.Name;
                clientOrderInfo.Ticket = 0;
                clientOrderInfo.Time = SystemTimeNow();
                clientOrderInfo.Symbol = symbol;
                clientOrderInfo.Side = (OrderSide)groupOrderInfo.Side;
                clientOrderInfo.Volume = clientVolume;
                clientOrderInfo.Price = groupOrderInfo.Price;
                clientOrderInfo.Comment = comment;
                clientOrderInfo.NewTicket = Constants.NO_TICKET;
                clientTradesForDB.Add(new DBClientTradeData(clientOrderInfo, false, 0, clientVolume, managerTradeID, distribution.Ratio));
            }

            // append all the clients' positions and trades to DB
            _DBAccess.AppendMultiClientPositions(clientPositionsForDB);
            _DBAccess.AppendMultiClientTrades(clientTradesForDB);
            // start open client trades
            ProcessOpenTrade_MT4(groupOrderInfo.Ticket, managerTradeID);
        }


        /// <summary>
        /// Open trades in MT4
        /// </summary>
        /// <param name="managerTicket"></param>
        /// <param name="managerTradeID"></param>
        private void ProcessOpenTrade_MT4(int managerTicket, long managerTradeID)
        {
            //_MTManager.KeepAlive();
            var sleepTime = DelayOpenTrades;
            var count = 0;
            var ticketsList = _PositionAdmin.GetClientPositions(managerTicket).Values.ToList(); ;

            SendAndWriteLog("Processor.ProcessOpenTrade_MT4 method", string.Format("Start open trades for manager ticket: {0}, trades to open: {1}", managerTicket, ticketsList.Count), MsgType.INFO, null);

            var execList = _PositionAdmin.GetClientPositions(managerTicket).Values.OrderBy(v => v.Login).ToList();

            foreach (var positionInfo in execList)
            {
                if (positionInfo.Status != PositionStatus.OPEN_NEW)
                {
                    continue;
                }

                // if client account is not our account make a pause on every round hour ~ 1 minute
                if (positionInfo.Login != 1300 && positionInfo.Login != 1400)
                {
                    if (DateTime.Now.Minute == 59 || DateTime.Now.Minute == 0)
                    {
                        var m = 2 - (DateTime.Now.Minute + 1) % 60;
                        Thread.Sleep((m * 60 - DateTime.Now.Second) * 1000);
                    }
                }

                string errorMsg = null;
                DateTime clientTime = SystemTimeNow();
                int clientTicket = _MTManager.OpenOrder(positionInfo.Login,
                                                           positionInfo.Side,
                                                           positionInfo.Symbol,
                                                           positionInfo.CurVolume,
                                                           positionInfo.OpenPrice,
                                                           positionInfo.Comment,
                                                           ref errorMsg,
                                                           ref clientTime);

                //if (clientTicket == Constants.ERROR_TICKET || clientTicket == Constants.NO_CONNECT || clientTicket == Constants.TRADE_NO_MONEY)
                if (clientTicket > 0)
                {
                    count++;
                    SendAndWriteLog("Processor.ProcessOpenTrade_MT4 method", string.Format("Open order for client {0} has been executed. Ticket = {1}.", positionInfo.Login, clientTicket), MsgType.INFO, null);
                    positionInfo.UpdateCreated(clientTicket, PositionStatus.OPEN_DONE);
                    _PositionAdmin.UpdateAcceptedClientTrade(managerTicket, positionInfo.Login, clientTicket, positionInfo.OpenPrice);
                }
                else
                {
                    // if the MT manager has not succeeded to open order for the client, 
                    // send and write the corresponding log message and pass to the next client
                    var msg = new StringBuilder("Open order hasn't been executed. ");
                    msg.Append("Error=" + errorMsg + ". ");
                    msg.Append("Client=" + positionInfo.Login + ", ");
                    msg.Append("Multiplier=" + positionInfo.Multiplier + ", ");
                    msg.Append("Symbol=" + positionInfo.Symbol + ", ");
                    msg.Append("Side=" + positionInfo.Side + ", ");
                    msg.Append("Volume=" + positionInfo.CurVolume + ", ");
                    msg.Append("Price=" + positionInfo.OpenPrice + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    if (clientTicket == Constants.TRADE_NO_MONEY || clientTicket == Constants.TRADE_BAD_VOLUME || clientTicket == Constants.INVALID_DATA)
                    {
                        SendAndWriteLog("Processor.ProcessOpenTrade_MT4 method", msg.ToString(), MsgType.WARNING, null);
                        //positionInfo.UpdateCreated(0, PositionStatus.OPEN_DONE);
                        
                        positionInfo.Status = PositionStatus.CLOSE_DONE_FULL;
                        _PositionAdmin.RemoveClientPosition_NotOpened(managerTicket, positionInfo.Login);
                        _DBAccess.RemoveClientPosition_NotOpened(managerTicket, positionInfo.Login);
                    }
                    else
                    {
                        SendAndWriteLog("Processor.ProcessOpenTrade_MT4 method", msg.ToString(), MsgType.ERROR, null);
                        positionInfo.UpdateCreated(0, PositionStatus.OPEN_IN_PROCESS);
                        if (clientTicket != Constants.ACCEPTED_TICKET)
                        {
                            positionInfo.Attempts++;
                            _DBAccess.UpdateClientPositionExecAttempts(managerTicket, positionInfo.Login, positionInfo.Attempts);
                            if (positionInfo.Attempts >= _MaxAttempts)
                            {
                                SendAndWriteLog("Processor.ProcessOpenTrade_MT4 method", "MAX ATTEMPTS: " + msg.ToString(), MsgType.ERROR, null);
                                positionInfo.Status = PositionStatus.CLOSE_DONE_FULL;
                                _PositionAdmin.RemoveClientPosition_NotOpened(managerTicket, positionInfo.Login);
                                _DBAccess.RemoveClientPosition_NotOpened(managerTicket, positionInfo.Login);
                            }
                            else
                            {
                                //MTManager_OnConnectionChanged((int)ConnType.DIRECT, false);
                                //_MTManager.KeepAlive();
                                break;
                            }
                        }
                    }
                }
                _DBAccess.UpdateOpenClientPosition(positionInfo.ManagerTicket, positionInfo.Login, positionInfo.CurTicket, (int)positionInfo.Status);
                _DBAccess.UpdateClientTrade(new DBClientTradeData(new OrderInfo()
                {
                    Ticket = positionInfo.CurTicket,
                    Login = positionInfo.Login,
                    Symbol = positionInfo.Symbol,
                    Side = positionInfo.Side,
                    Volume = positionInfo.CurVolume,
                    Price = positionInfo.OpenPrice
                }, false, positionInfo.CurTicket, -1, -1, -1));

                Thread.Sleep(sleepTime);
                sleepTime += DelayOpenTradesIncrement;
            }

            SendAndWriteLog("Processor.ProcessOpenTrade_MT4 method", string.Format("End open trades for manager ticket: {0}, trades was open: {1}", managerTicket, count), MsgType.INFO, null);

            FinishOpenTrade(managerTicket);
        }

        private void FinishOpenTrade(int managerTicket)
        {
            var hasNotFinished = _PositionAdmin.GetClientPositions(managerTicket).Values.Any(it => it.Status != PositionStatus.OPEN_DONE);
            if (!hasNotFinished)
            {
                _PositionAdmin.GetManagerPosition(managerTicket).Status = PositionStatus.OPEN_DONE;
                _DBAccess.UpdateManagerPosition(managerTicket, (int)PositionStatus.OPEN_DONE);
            }
        }


        /// <summary>
        /// Catches a close trade, saves it in the trade queue and signals the trade processing thread
        /// </summary>
        /// <param name="orderInfo"></param>
        private void MTManager_OnCloseTrade(OrderInfo orderInfo)
        {
            lock (_TradeQueueLocker)
            {
                _TradeQueue.Enqueue(new TradeData(true, orderInfo));                
            }

            _NewTradeEvent.Set();          
        }

        /// <summary>
        /// Processes a close trade
        /// </summary>
        /// <param name="orderInfo"></param>
        /// <param name="extendedLog"></param>
        private void ProcessCloseTrade(OrderInfo orderInfo, bool extendedLog, string sourceMsg)
        {
            // look for the trade ticket in the position admin AS MANAGER
            if (_PositionAdmin.ManagerTicketExists(orderInfo.Ticket))
            {
                if (!string.IsNullOrEmpty(sourceMsg))
                {
                    SendAndWriteLog("Processor.ProcessCloseTrade method", sourceMsg, MsgType.WARNING, null);
                }
                ProcessManagerCloseTrade(orderInfo, extendedLog);
            }

            // look for the trade ticket in the position admin AS CLIENT
            if (_PositionAdmin.ClientTicketExists(orderInfo.Ticket))
            {
                if (!string.IsNullOrEmpty(sourceMsg))
                {
                    SendAndWriteLog("Processor.ProcessCloseTrade method", sourceMsg, MsgType.WARNING, null);
                }
                ProcessClientCloseTrade(orderInfo);
            }
        }

        /// <summary>
        /// Processes a manager close trade
        /// </summary>
        /// <param name="managerOrderInfo"></param>
        /// <param name="extendedLog"></param>
        private void ProcessManagerCloseTrade(OrderInfo managerOrderInfo, bool extendedLog)
        {
            StringBuilder msg;
            double partialCloseRatio = 1;

            int managerUpdatedTicket;
            int managerOrigTicket;
            int managerRemainingVolume;
            long managerTradeID;

            if (extendedLog)
            {
                msg = new StringBuilder("Processing manager close order. ");
                msg.Append("Manager=" + managerOrderInfo.Login + ", ");
                msg.Append("Group=" + managerOrderInfo.Group + ", ");
                msg.Append("Ticket=" + managerOrderInfo.Ticket + ", ");
                msg.Append("Symbol=" + managerOrderInfo.Symbol + ", ");
                msg.Append("Side=" + managerOrderInfo.Side + ", ");
                msg.Append("Volume=" + managerOrderInfo.Volume + ", ");
                msg.Append("Price=" + managerOrderInfo.Price + ", ");
                msg.Append("NewTicket=" + managerOrderInfo.NewTicket + ", ");
                msg.Append("Comment=" + managerOrderInfo.Comment + ".");

                SendAndWriteLog("Processor.ProcessManagerCloseTrade method", msg.ToString(), MsgType.INFO, null);
            }

            if ((OrderSide)managerOrderInfo.Side != OrderSide.BUY && (OrderSide)managerOrderInfo.Side != OrderSide.SELL)
            {
                SendAndWriteLog("Processor.ProcessManagerCloseTrade method", "Incorrect order side.", MsgType.ERROR, null);
                return;
            }

            var curManagerPos = _PositionAdmin.GetManagerPosition(managerOrderInfo.Ticket);
            if (curManagerPos.Status != PositionStatus.OPEN_DONE && curManagerPos.Status != PositionStatus.CLOSE_DONE_PARTIAL)
            {
                SendAndWriteLog("Processor.ProcessManagerCloseTrade method", "Not all client tickets have been opened / partial closed.", MsgType.ERROR, null);
                return;
            }

            int curManagerPosVolume = curManagerPos.CurVolume;
            List<FairItem> clientPartialCloseVolumes = new List<FairItem>();
            // check if the ticket position has been fully closed
            bool fullyClosed = (curManagerPosVolume == managerOrderInfo.Volume);
            bool closeHedgeLargerOrder = false;

            if (managerOrderInfo.Volume == 0) //close hedge child
            {
                managerOrderInfo.Volume = curManagerPosVolume;
                fullyClosed = true;
            }

            int managerCloseVolume = managerOrderInfo.Volume;

            if (curManagerPosVolume > 0)
            {
                if (curManagerPosVolume < managerOrderInfo.Volume)
                {
                    // If we are here, something goes wrong. Normally, we never get here.   
                    msg = new StringBuilder("The volume to be closed is greater than the current volume. ");

                    msg.Append("Manager=" + managerOrderInfo.Login + ", ");
                    msg.Append("Group=" + managerOrderInfo.Group + ", ");
                    msg.Append("Ticket=" + managerOrderInfo.Ticket + ", ");
                    msg.Append("Symbol=" + managerOrderInfo.Symbol + ", ");
                    msg.Append("Side=" + managerOrderInfo.Side + ", ");
                    msg.Append("Volume=" + managerOrderInfo.Volume + ", ");
                    msg.Append("Price=" + managerOrderInfo.Price + ", ");
                    msg.Append("CurrentVolume=" + curManagerPosVolume + ".");

                    SendAndWriteLog("Processor.ProcessManagerCloseTrade method", msg.ToString(), MsgType.ERROR, null);

                    return;
                }

                if (!fullyClosed)
                {
                    //update the partial close ratio
                    if (curManagerPosVolume > 0)
                    {
                        partialCloseRatio = (double)managerOrderInfo.Volume / curManagerPosVolume;
                        if (partialCloseRatio > 1)
                        {
                            SendAndWriteLog("Processor.ProcessManagerCloseTrade method", string.Format("Invalid Partial Close Ratio{0}, is bigger than 1 setting to  default 1", partialCloseRatio), MsgType.ERROR, null);
                            partialCloseRatio = 1;
                        }
                        if (managerOrderInfo.NewTicket == 0) //close hedge larger order: manager -> close full, clients -> close partial and new ticket close full to zero
                        {
                            closeHedgeLargerOrder = true;
                            managerOrderInfo.Volume = curManagerPosVolume;
                        }
                    }
                    else
                    {
                        SendAndWriteLog("Processor.ProcessManagerCloseTrade method", string.Format("Invalid Manager Current Position Volume {0}", curManagerPosVolume), MsgType.ERROR, null);
                    }
                }
                // update the manager position in the position admin
                _PositionAdmin.UpdateManagerPosition(managerOrderInfo.Ticket, managerOrderInfo.NewTicket, managerOrderInfo.Volume);

                if (fullyClosed || closeHedgeLargerOrder)
                {
                    // in this case the manager ticket hasn't been updated by PositionAdmin, so we don't use the new ticket
                    managerUpdatedTicket = managerOrderInfo.Ticket;
                }
                else
                {
                    // here the new ticket is used
                    managerUpdatedTicket = managerOrderInfo.NewTicket;
                }
                // update the manager position in DB (we apply 'append' instead of 'update' 
                // because the current position defined by the last record among the records having the same original ticket)
                _DBAccess.AppendManagerPosition(_PositionAdmin.GetManagerPosition(managerUpdatedTicket));

                managerOrigTicket = _PositionAdmin.GetManagerPosition(managerUpdatedTicket).OrigTicket;
                managerRemainingVolume = _PositionAdmin.GetManagerPosition(managerUpdatedTicket).CurVolume;

                // append the manager trade to DB and obtain its ID
                managerTradeID = _DBAccess.AppendManagerTrade(managerOrderInfo, true, managerOrigTicket, managerRemainingVolume);
            }
            else if (curManagerPosVolume == 0)
            {
                managerUpdatedTicket = managerOrderInfo.Ticket;
                managerOrigTicket = _PositionAdmin.GetManagerPosition(managerUpdatedTicket).OrigTicket;
                managerRemainingVolume = _PositionAdmin.GetManagerPosition(managerUpdatedTicket).CurVolume;
                managerTradeID = _DBAccess.GetManagerMaxTradeID(managerOrigTicket);
            }
            else
            {
                SendAndWriteLog("Processor.ProcessManagerCloseTrade method", "The current volume is less then zero.", MsgType.INFO, null);
                return;
            }

            // get all the client positions caused by the manager position
            var clientPositions = _PositionAdmin.GetClientPositions(managerUpdatedTicket);

            if (extendedLog)
            {
                SendAndWriteLog("Processor.ProcessManagerCloseTrade method", "ManagerTradeID=" + managerTradeID + ", Clients=" + clientPositions.Count, MsgType.INFO, null);
            }

            if (clientPositions.Count == 0) // no client positions            
            {
                FinishCloseTrade(managerUpdatedTicket);
                return;
            }

            string symbol = managerOrderInfo.Symbol;
            OrderSide side = (OrderSide)managerOrderInfo.Side;
            double price = managerOrderInfo.Price;

            List<DBClientTradeData> clientTradesForDB = new List<DBClientTradeData>(clientPositions.Count); // client trades to be written to DB 
            List<DBClientPositionData> clientPositionsForDB = new List<DBClientPositionData>(clientPositions.Count); // client position to be written to DB
            List<int> clientOrigTicketsToRemoveFromDB = new List<int>(clientPositions.Count);  // client original tickets to be removed from DB table client_positions

            if (!fullyClosed)
            {
                //calculate the fair distribution of the clients position partial close volumes by their account multiplier(percentage)
                foreach (PositionInfo clientPosition in clientPositions.Values)
                {
                    FairItem newItem = new FairItem() { ID = clientPosition.Login, Ratio = clientPosition.Multiplier, Result = clientPosition.CurVolume };
                    clientPartialCloseVolumes.Add(newItem);
                }
                FairDistribution.PartialCloseDistribute(managerCloseVolume, partialCloseRatio, ref clientPartialCloseVolumes);
            }

            SendAndWriteLog("Processor.ProcessManagerCloseTrade method", "Start close client tickets", MsgType.INFO, null);

            foreach (int clientTicket in clientPositions.Keys.OrderBy(k => k).ToList())
            {
                //SendAndWriteLog("Processor.ProcessManagerCloseTrade method", "Close client ticket #" + clientTicket, MsgType.INFO, null);
                int clientLogin = clientPositions[clientTicket].Login;
                int clientPrevVolume = clientPositions[clientTicket].CurVolume;
                int clientOrigTicket = clientPositions[clientTicket].OrigTicket;
                double clientMultiplier = clientPositions[clientTicket].Multiplier;
                string clientSymbol = clientPositions[clientTicket].Symbol;
                int clientClosedVolume;

                if (fullyClosed)
                {
                    // close all the remaining volume                                        
                    clientClosedVolume = clientPrevVolume;
                }
                else
                {
                    clientClosedVolume = clientPartialCloseVolumes.Where((c) => c.ID == clientLogin).FirstOrDefault().Result;
                    //clientClosedVolume = (int)Math.Round(managerOrderInfo.Volume * clientMultiplier);
                }

                if (extendedLog)
                {
                    msg = new StringBuilder("Sending client close order. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("Ticket=" + clientTicket + ", ");
                    msg.Append("Symbol=" + clientSymbol + ", ");
                    msg.Append("Side=" + side + ", ");
                    msg.Append("Volume=" + clientClosedVolume + ", ");
                    msg.Append("Price=" + price + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    SendAndWriteLog("Processor.ProcessManagerCloseTrade method", msg.ToString(), MsgType.INFO, null);
                }

                if (clientClosedVolume > clientPrevVolume)
                {
                    msg = new StringBuilder("client close order ERROR: Close Volume Bigger Than Actual. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("Ticket=" + clientTicket + ", ");
                    msg.Append("Symbol=" + clientSymbol + ", ");
                    msg.Append("Side=" + side + ", ");
                    msg.Append("Volume=" + clientClosedVolume + ", ");
                    msg.Append("Actual Volume=" + clientPrevVolume + ", ");
                    msg.Append("Price=" + price + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    SendAndWriteLog("Processor.ProcessManagerCloseTrade method", msg.ToString(), MsgType.ERROR, null);
                    continue; // skip to the next client position
                }
                if (clientClosedVolume == 0)
                {
                    msg = new StringBuilder("Abort Sending Close Order : Close Volume = 0. ");
                    msg.Append("Client=" + clientLogin + ", ");
                    msg.Append("Ticket=" + clientTicket + ", ");
                    msg.Append("Symbol=" + clientSymbol + ", ");
                    msg.Append("Side=" + side + ", ");
                    msg.Append("Volume=" + clientClosedVolume + ", ");
                    msg.Append("Actual Volume=" + clientPrevVolume + ", ");
                    msg.Append("Price=" + price + ", ");
                    msg.Append("ManagerTradeID=" + managerTradeID + ".");

                    SendAndWriteLog("Processor.ProcessManagerCloseTrade method", msg.ToString(), MsgType.INFO, null);
                    continue; // skip to the next client position
                }


                // prepare the client trade for appending to DB
                OrderInfo clientOrderInfo = new OrderInfo();
                clientOrderInfo.Login = clientLogin;
                clientOrderInfo.Ticket = clientTicket;
                clientOrderInfo.Time = SystemTimeNow();
                clientOrderInfo.Symbol = clientSymbol;
                clientOrderInfo.Side = side;
                clientOrderInfo.Volume = clientClosedVolume;
                clientOrderInfo.Price = price;
                clientOrderInfo.Comment = string.Format("MAM Closed {0} #{1} ({2})", managerOrderInfo.Login, managerOrderInfo.Ticket, managerOrderInfo.Group);
                clientOrderInfo.NewTicket = 0;
                clientTradesForDB.Add(new DBClientTradeData(clientOrderInfo, true, clientOrigTicket, clientPrevVolume - clientClosedVolume, managerTradeID, clientMultiplier));


                clientPositions[clientTicket].UpdateToClose(clientClosedVolume, price, fullyClosed, closeHedgeLargerOrder);
                clientPositionsForDB.Add(new DBClientPositionData(managerUpdatedTicket, clientPositions[clientTicket]));
            }

            SendAndWriteLog("Processor.ProcessManagerCloseTrade method", "End close client tickets", MsgType.INFO, null);

            // append all the executed client trades to DB
            _DBAccess.AppendMultiClientTrades(clientTradesForDB);

            _DBAccess.AppendMultiClientPositions(clientPositionsForDB);

            // start close client trades
            ProcessCloseTrade_MT4(managerUpdatedTicket, managerTradeID);
        }

        
        /// <summary>
        /// Close trades in MT4
        /// </summary>
        /// <param name="managerTicket"></param>
        /// <param name="managerTradeID"></param>
        private void ProcessCloseTrade_MT4(int managerTicket, long managerTradeID)
        {
            //_MTManager.KeepAlive();
            var sleepTime = DelayOpenTrades;

            var mng = _PositionAdmin.GetManagerPosition(managerTicket);
            if (mng == null)
            {
                SendAndWriteLog("Processor.ProcessCloseTrade_MT4 method", "Manager not found by manager ticket: " + managerTicket, MsgType.ERROR, null);
                return;
            }

            var execList = _PositionAdmin.GetClientPositions(managerTicket).Values.OrderBy(v => v.Login).ToList();

            foreach (var positionInfo in execList)
            {
                if (positionInfo.Status != PositionStatus.CLOSE_NEW && positionInfo.Status != PositionStatus.CLOSE_IN_PROCESS)
                {
                    continue;
                }

                // if client account is not our account make a pause on every round hour ~ 1 minute
                if (positionInfo.Login != 1300 && positionInfo.Login != 1400)
                {
                    if (DateTime.Now.Minute == 59 || DateTime.Now.Minute == 0)
                    {
                        var m = 2 - (DateTime.Now.Minute + 1) % 60;
                        Thread.Sleep((m * 60 - DateTime.Now.Second) * 1000);
                    }
                }

                string errorMsg = null;
                DateTime clientTime = SystemTimeNow();

                if (positionInfo.Status == PositionStatus.CLOSE_NEW)
                {
                    int clientNewTicket = _MTManager.CloseOrder(positionInfo.CurTicket,
                                                                positionInfo.Side,
                                                                positionInfo.Symbol,
                                                                positionInfo.CloseVolume,
                                                                positionInfo.ClosePrice,
                                                                string.Format("MAM Closed {0} #{1}", mng.Login, managerTicket),
                                                                !positionInfo.FullClose,
                                                                ref errorMsg,
                                                                ref clientTime);

                    if (clientNewTicket < 0)
                    {
                        // if the MT manager has not succeeded to close order for the client, 
                        // send and write the corresponding log message and pass to the next client
                        var msg = new StringBuilder("Close order hasn't been executed. ");
                        msg.Append("Error=" + errorMsg + ". ");
                        msg.Append("Client=" + positionInfo.Login + ", ");
                        msg.Append("Ticket=" + positionInfo.CurTicket + ", ");
                        msg.Append("Symbol=" + positionInfo.Symbol + ", ");
                        msg.Append("Side=" + positionInfo.Side + ", ");
                        msg.Append("Volume=" + positionInfo.CloseVolume + ", ");
                        msg.Append("Price=" + positionInfo.ClosePrice + ", ");
                        msg.Append("ManagerTradeID=" + managerTradeID + ".");

                        SendAndWriteLog("Processor.ProcessCloseTrade_MT4 method", msg.ToString(), MsgType.ERROR, null);

                        //MTManager_OnConnectionChanged((int)ConnType.DIRECT, false);
                        //_MTManager.KeepAlive();
                        break;
                    }
                    else
                    {
                        var msg = new StringBuilder("Close ticket = " + positionInfo.CurTicket + " for client " + positionInfo.Login + " has been executed. ");
                        if (clientNewTicket != Constants.NO_TICKET)
                        {
                            msg.Append("NewTicket = " + clientNewTicket + ".");
                        }

                        SendAndWriteLog("Processor.ProcessCloseTrade_MT4 method", msg.ToString(), MsgType.INFO, null);

                        _DBAccess.UpdateClientTrade(new DBClientTradeData(new OrderInfo()
                            {
                                Ticket = positionInfo.CurTicket,
                                Login = positionInfo.Login,
                                Symbol = positionInfo.Symbol,
                                Side = positionInfo.Side,
                                Volume = positionInfo.CloseVolume,
                                Price = positionInfo.ClosePrice,
                                NewTicket = clientNewTicket
                            }, true, positionInfo.OrigTicket, positionInfo.CurVolume - positionInfo.CloseVolume, managerTradeID, positionInfo.Multiplier));

                        if ((positionInfo.CurVolume - positionInfo.CloseVolume) == 0)
                        {
                            positionInfo.Status = PositionStatus.CLOSE_DONE_FULL;
                            // the client ticket hasn't been updated, so we can refer to the client position by the old ticket
                            // remove the client position from the position admin
                            _PositionAdmin.RemoveClientPosition(positionInfo.Login, positionInfo.CurTicket);

                            // remove client position from DB
                            _DBAccess.RemoveClientPosition(positionInfo.OrigTicket);

                            continue;
                        }
                        else
                        {
                            positionInfo.Status = (positionInfo.CloseHedge ? PositionStatus.CLOSE_IN_PROCESS : (positionInfo.FullClose ? PositionStatus.CLOSE_DONE_FULL : PositionStatus.CLOSE_DONE_PARTIAL));
                            _PositionAdmin.UpdateClientPosition(positionInfo.CurTicket, clientNewTicket, positionInfo.CloseVolume);
                            if (clientNewTicket <= 0)
                            {
                                //TODO PARTIAL CLOSE than account 1300
                            }
                            _DBAccess.UpdateCloseClientPosition(managerTicket, positionInfo.Login, clientNewTicket, (int)positionInfo.Status);
                        }
                    }
                }

                if (positionInfo.Status == PositionStatus.CLOSE_IN_PROCESS)
                {
                    if (positionInfo.CloseHedge)
                    {
                        #region Close Hedge

                        if (positionInfo.CurVolume == 0)
                        {
                            var msg = new StringBuilder("Abort Sending Close Order : Close Volume = 0. ");
                            msg.Append("Client=" + positionInfo.Login + ", ");
                            msg.Append("Ticket=" + positionInfo.CurTicket + ", ");
                            msg.Append("Symbol=" + positionInfo.Symbol + ", ");
                            msg.Append("Side=" + positionInfo.Side + ", ");
                            msg.Append("Volume=" + positionInfo.CurVolume + ", ");
                            msg.Append("Price=" + positionInfo.OpenPrice + ", ");
                            msg.Append("ManagerTradeID=" + managerTradeID + ".");

                            SendAndWriteLog("Processor.ProcessCloseTrade_MT4 method", msg.ToString(), MsgType.INFO, null);
                            continue; // skip to the next client position
                        }

                        int closeResponse = _MTManager.CloseOrder(positionInfo.CurTicket,
                                                                    positionInfo.Side,
                                                                    positionInfo.Symbol,
                                                                    positionInfo.CurVolume,
                                                                    positionInfo.OpenPrice,
                                                                    string.Format("MAM Closed {0} #{1}", mng.Login, managerTicket),
                                                                    false,
                                                                    ref errorMsg,
                                                                    ref clientTime);

                        if (closeResponse < 0)
                        {
                            // if the MT manager has not succeeded to close order for the client, 
                            // send and write the corresponding log message and pass to the next client
                            var msg = new StringBuilder("Close order hasn't been executed. ");
                            msg.Append("Error=" + errorMsg + ". ");
                            msg.Append("Client=" + positionInfo.Login + ", ");
                            msg.Append("Ticket=" + positionInfo.CurTicket + ", ");
                            msg.Append("Symbol=" + positionInfo.Symbol + ", ");
                            msg.Append("Side=" + positionInfo.Side + ", ");
                            msg.Append("Volume=" + positionInfo.CurVolume + ", ");
                            msg.Append("Price=" + positionInfo.OpenPrice + ", ");
                            msg.Append("ManagerTradeID=" + managerTradeID + ".");

                            SendAndWriteLog("Processor.ProcessCloseTrade_MT4 method", msg.ToString(), MsgType.ERROR, null);

                            //MTManager_OnConnectionChanged((int)ConnType.DIRECT, false);
                            //_MTManager.KeepAlive();
                            break;
                        }
                        else
                        {
                            var msg = new StringBuilder("Close ticket = " + positionInfo.CurTicket + " for client " + positionInfo.Login + " has been executed. ");
                            SendAndWriteLog("Processor.ProcessCloseTrade_MT4 method", msg.ToString(), MsgType.INFO, null);

                            // prepare the client trade for appending to DB
                            OrderInfo clientOrderInfo = new OrderInfo();
                            clientOrderInfo.Login = positionInfo.Login;
                            clientOrderInfo.Ticket = positionInfo.CurTicket;
                            clientOrderInfo.Time = clientTime;
                            clientOrderInfo.Symbol = positionInfo.Symbol;
                            clientOrderInfo.Side = positionInfo.Side;
                            clientOrderInfo.Volume = positionInfo.CurVolume;
                            clientOrderInfo.Price = positionInfo.OpenPrice;
                            clientOrderInfo.Comment = string.Format("MAM Closed {0} #{0}", mng.Login, managerTicket);
                            clientOrderInfo.NewTicket = 0;
                            _DBAccess.AppendClientTrade(clientOrderInfo, true, positionInfo.OrigTicket, 0, managerTradeID, positionInfo.Multiplier);

                            positionInfo.Status = PositionStatus.CLOSE_DONE_FULL;
                            // the client ticket hasn't been updated, so we can refer to the client position by the old ticket
                            // remove the client position from the position admin
                            _PositionAdmin.RemoveClientPosition(positionInfo.Login, positionInfo.CurTicket);

                            // remove client position from DB
                            _DBAccess.RemoveClientPosition(positionInfo.OrigTicket);
                        }

                        #endregion Close Hedge
                    }
                }

                Thread.Sleep(sleepTime);
                sleepTime += DelayOpenTradesIncrement;
            }

            FinishCloseTrade(managerTicket);
        }


        private void FinishCloseTrade(int managerTicket)
        {
            var hasNotFinished = _PositionAdmin.GetClientPositions(managerTicket).Values.Any(it => it.Status == PositionStatus.CLOSE_NEW || it.Status == PositionStatus.OPEN_IN_PROCESS);
            if (!hasNotFinished)
            {
                //_PositionAdmin.GetManagerPosition(managerTicket).Status = PositionStatus.CLOSE_DONE;
                //_DBAccess.UpdateManagerPosition(managerTicket, (int)PositionStatus.CLOSE_DONE);
                var managerPosition = _PositionAdmin.GetManagerPosition(managerTicket);
                if (managerPosition.FullClose || managerPosition.CurVolume == 0)
                {
                    if (_PositionAdmin.ClientPositionsCountByManagerTicket(managerTicket) == 0)
                    {
                        _PositionAdmin.RemoveManagerPosition(managerTicket);
                        _DBAccess.RemoveManagerPosition(managerPosition.OrigTicket);
                    }
                }
                else
                {
                    managerPosition.Status = PositionStatus.CLOSE_DONE_PARTIAL;
                    _DBAccess.UpdateManagerPosition(managerTicket, (int)PositionStatus.CLOSE_DONE_PARTIAL);
                }
            }
        }


        /// <summary>
        /// Processes a managed trade closed (fully or partially) by the client.
        /// </summary>
        /// <param name="clientOrderInfo"></param>
        /// <param name="managerLogin"></param>
        private void ProcessClientCloseTrade(OrderInfo clientOrderInfo)
        {
            if ((OrderSide)clientOrderInfo.Side != OrderSide.BUY && (OrderSide)clientOrderInfo.Side != OrderSide.SELL)
            {
                SendAndWriteLog("Processor.ProcessClientCloseTrade method", "Incorrect order side.", MsgType.ERROR, null);
                return;
            }

            int managerCurTicket;

            managerCurTicket = _PositionAdmin.GetManagerTicketByClient(clientOrderInfo.Ticket);
            int managerOrigTicket = _PositionAdmin.GetManagerPosition(managerCurTicket).OrigTicket;

            PositionInfo clientPositionInfo = _PositionAdmin.GetClientPosition(clientOrderInfo.Ticket);
            int remainingVolume = clientPositionInfo.CurVolume - clientOrderInfo.Volume;

            if (clientPositionInfo.Status == PositionStatus.CLOSE_NEW || clientPositionInfo.Status == PositionStatus.CLOSE_IN_PROCESS)
            {
                if (clientPositionInfo.Status == PositionStatus.CLOSE_NEW)
                {
                    clientPositionInfo.Status = (clientPositionInfo.CloseHedge ? PositionStatus.CLOSE_IN_PROCESS : (clientPositionInfo.FullClose ? PositionStatus.CLOSE_DONE_FULL : PositionStatus.CLOSE_DONE_PARTIAL));
                }
                else
                {
                    clientPositionInfo.Status = (clientPositionInfo.FullClose ? PositionStatus.CLOSE_DONE_FULL : PositionStatus.CLOSE_DONE_PARTIAL);
                }
                _PositionAdmin.UpdateClientPosition(clientOrderInfo.Ticket, clientOrderInfo.NewTicket, clientPositionInfo.CloseVolume);
                if (clientOrderInfo.NewTicket <= 0)
                {
                    ///TODO PARTIAL CLOSE than account 1300
                }
                _DBAccess.UpdateCloseClientPosition(managerCurTicket, clientOrderInfo.Login, clientOrderInfo.NewTicket, (int)clientPositionInfo.Status);
            }
            else
            {
                StringBuilder msg = new StringBuilder("Position has been closed by client. ");
                msg.Append("Client=" + clientOrderInfo.Login + ", ");
                msg.Append("Ticket=" + clientOrderInfo.Ticket + ", ");
                msg.Append("Symbol=" + clientOrderInfo.Symbol + ", ");
                msg.Append("Side=" + clientOrderInfo.Side + ", ");
                msg.Append("Volume=" + clientOrderInfo.Volume + ", ");
                msg.Append("Price=" + clientOrderInfo.Price + ".");

                SendAndWriteLog("Processor.ProcessClientCloseTrade method", msg.ToString(), MsgType.INFO, null);

                // alter the comment
                StringBuilder newComment = new StringBuilder("Closed by client!");
                if (!string.IsNullOrEmpty(clientOrderInfo.Comment))
                {
                    newComment.Append(" ");
                    newComment.Append(clientOrderInfo.Comment);
                }
                clientOrderInfo.Comment = newComment.ToString();

                // remove the client position from the position admin and DB (also in the case of partial close)
                _PositionAdmin.RemoveClientPosition(clientOrderInfo.Login, clientOrderInfo.Ticket);
                _DBAccess.RemoveClientPosition(clientPositionInfo.OrigTicket);

                // append the client trade to DB
                _DBAccess.AppendClientTrade(clientOrderInfo, true, clientPositionInfo.OrigTicket, 0, 0, 0);
            }

            FinishCloseTrade(managerCurTicket);
        }

        /// <summary>
        /// Appends a new group
        /// </summary>
        /// <param name="name"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool AppendGroup(string name)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.AppendGroup method", "It's forbidden to add group when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (!_Groups.IsEmpty && _Groups.ContainsKey(name))
                    {
                        SendAndWriteLog("Processor.AppendGroup method", "The group already exists.", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        _Groups[name] = new Group(name, SystemTimeNow());

                        _DBAccess.AppendGroup(name, SystemTimeNow());

                        string logMsg = string.Format("Group {0} has been appended.", name);
                        SendAndWriteLog("Processor.AppendGroup method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.AppendManager method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }

        /// <summary>
        /// Appends a new client to an existing group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="clientLogin"></param>
        /// <param name="clientName"></param>
        /// <param name="multiplier"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool AppendClientGr(string groupName, int clientLogin, string clientName, double multiplier)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.AppendClientGr method", "It's forbidden to add clients when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Groups.IsEmpty || !_Groups.ContainsKey(groupName))
                    {
                        SendAndWriteLog("Processor.AppendClientGr method", "The group doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Groups[groupName].ClientExists(clientLogin))
                    {
                        SendAndWriteLog("Processor.AppendClientGr method", "The client already exists.", MsgType.INFO, null);
                        return false;
                    }
                    else if (multiplier == 0)
                    {
                        SendAndWriteLog("Processor.AppendClientGr method", "Multiplier cannot be 0", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        DateTime systemTimeNow = SystemTimeNow();

                        _Groups[groupName].AppendClient(new Client(clientLogin, clientName, multiplier, systemTimeNow, systemTimeNow));

                        _DBAccess.AppenGroupSubscription(groupName, clientLogin, clientName, multiplier, systemTimeNow);

                        string logMsg = string.Format("Client {0} ({1}) has been appended to group {2} with multiplier {3}.", clientLogin, clientName, groupName, multiplier);
                        SendAndWriteLog("Processor.AppendClientGr method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }  
            catch (Exception e)
            {
                SendAndWriteLog("Processor.AppendClient method", e.Message, MsgType.ERROR, e);
                return false;
            }            
        }

        /// <summary>
        /// Removes a group by his name
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool RemoveGroup(string groupName)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.RemoveGroup method", "It's forbidden to remove groups when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Groups.IsEmpty || !_Groups.ContainsKey(groupName))
                    {
                        SendAndWriteLog("Processor.RemoveGroup method", "The group doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Groups[groupName].Clients.Count > 0)
                    {
                        SendAndWriteLog("Processor.RemoveGroup method", "The group has clients. He cannot be removed.", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        string logMsg;

                        // remove all the subscriptions by nullifying their multipliers
                        foreach (int clientLogin in _Groups[groupName].Clients.Keys)
                        {
                            _DBAccess.RemoveGroupSubscription(groupName, clientLogin, _Groups[groupName].GetClient(clientLogin).Name, 0, SystemTimeNow());

                            logMsg = string.Format("Client {0} has been removed from group {1}.", clientLogin, groupName);
                            SendAndWriteLog("Processor.RemoveGroup method", logMsg, MsgType.INFO, null);
                        }

                        //_PositionAdmin.RemoveManager();

                        Group removedGroup;
                        if (!_Groups.TryRemove(groupName, out removedGroup))
                        {
                            throw new Exception(string.Format("Can't remove group {0} from the list", groupName));
                        }

                        _DBAccess.RemoveGroup(groupName);

                        logMsg = string.Format("Group {0} has been removed.", groupName);
                        SendAndWriteLog("Processor.RemoveGroup method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.RemoveManager method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }

        /// <summary>
        /// Removes a subscription by the group name and client login
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="clientLogin"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool RemoveClientGr(string groupName, int clientLogin)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.RemoveClientGr method", "It's forbidden to remove clients when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Groups.IsEmpty || !_Groups.ContainsKey(groupName))
                    {
                        SendAndWriteLog("Processor.RemoveClientGr method", "The manager doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (!_Groups[groupName].ClientExists(clientLogin))
                    {
                        SendAndWriteLog("Processor.RemoveClientGr method", "The client doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        string clientName = GetClientGrName(groupName, clientLogin);
                        
                        //var clientOrigTickets = _PositionAdmin.RemoveClient(clientLogin);
                        //_DBAccess.RemoveMultiClientPositions(clientOrigTickets);

                        _Groups[groupName].RemoveClient(clientLogin);
                        _DBAccess.RemoveGroupSubscription(groupName, clientLogin, clientName, 0, SystemTimeNow());

                        string logMsg = string.Format("Client {0} has been removed from group {1}.", clientLogin, groupName);
                        SendAndWriteLog("Processor.RemoveClientGr method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.RemoveClientGr method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }

        /// <summary>
        /// Updates a client multiplier 
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="clientLogin"></param>
        /// <param name="multiplier"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool UpdateClientGr(string groupName, int clientLogin, double multiplier)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.UpdateClientGr method", "It's forbidden to update clients when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Groups.IsEmpty || !_Groups.ContainsKey(groupName))
                    {
                        SendAndWriteLog("Processor.UpdateClientGr method", "The group doesn't exist", MsgType.INFO, null);
                        return false;
                    }
                    else if (!_Groups[groupName].ClientExists(clientLogin))
                    {
                        SendAndWriteLog("Processor.UpdateClientGr method", "The client doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (multiplier == _Groups[groupName].GetClient(clientLogin).Multiplier)
                    {
                        SendAndWriteLog("Processor.UpdateClientGr method", "The new multiplier coincides with the old one.", MsgType.INFO, null);
                        return false;
                    }
                    else if (multiplier == 0)
                    {
                        SendAndWriteLog("Processor.UpdateClientGr method", "The multiplier cannot be 0.", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        DateTime systemTimeNow = SystemTimeNow();
                        _Groups[groupName].GetClient(clientLogin).ChangeMultiplier(multiplier, systemTimeNow);
                        string clientName = GetClientGrName(groupName, clientLogin);

                        _DBAccess.UpdateGroupSubscription(groupName, clientLogin, clientName, multiplier, systemTimeNow);

                        string logMsg = string.Format("Client {0} has changed his multiplier for group {1}. The new multiplier: {2}.", clientLogin, groupName, multiplier);
                        SendAndWriteLog("Processor.UpdateClientGr method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.UpdateClientGr method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }

        /// <summary>
        /// Gets deep clone of all the groups
        /// </summary>
        /// <returns>deep cloned groups, which can be accessed by their names</returns>
        public Dictionary<string, Group> GetGroups()
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    var deepClonedGroups = new Dictionary<string, Group>();

                    foreach (Group group in _Groups.Values)
                    {
                        deepClonedGroups.Add(group.Name, group.GetDeepClone());
                    }

                    return deepClonedGroups;
                }
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.GetGroups method", e.Message, MsgType.ERROR, e);
                return null;
            }
        }

        /// <summary>
        /// Gets a client name by his login and his group name.
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="clientLogin"></param>
        /// <returns>client name or null (if the specified client is not subscribed to the specified manager)</returns>
        private string GetClientGrName(string groupName, int clientLogin)
        {
            if (_Groups.IsEmpty || !_Groups.ContainsKey(groupName))
            {
                return null;
            }
            else if (!_Groups[groupName].ClientExists(clientLogin))
            {
                return null;
            }
            else
            {
                return _Groups[groupName].GetClient(clientLogin).Name;
            }
        }

        /// <summary>
        /// Appends a new manager
        /// </summary>
        /// <param name="login"></param>
        /// <param name="name"></param>
        /// <param name="password"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool AppendManager(int login, string name, string password)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.AppendManager method", "It's forbidden to add managers when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (!_Managers.IsEmpty && _Managers.ContainsKey(login))
                    {
                        SendAndWriteLog("Processor.AppendManager method", "The manager already exists.", MsgType.INFO, null);
                        return false;
                    }                   
                    else
                    {
                        _Managers[login] = new Manager(login, name, password, SystemTimeNow(), null, null);
                        
                        _DBAccess.AppendManager(login, name, password, SystemTimeNow());
                        
                        string logMsg = string.Format("Manager {0} ({1}) has been appended.", login, name);
                        SendAndWriteLog("Processor.AppendManager method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }  
            catch (Exception e)
            {
                SendAndWriteLog("Processor.AppendManager method", e.Message, MsgType.ERROR, e);
                return false;
            }            
        }

        /// <summary>
        /// Appends a new client to an existing manager
        /// </summary>
        /// <param name="managerLogin"></param>
        /// <param name="clientLogin"></param>
        /// <param name="clientName"></param>
        /// <param name="multiplier"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool AppendClient(int managerLogin, int clientLogin, string clientName, double multiplier)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.AppendClient method", "It's forbidden to add clients when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (managerLogin == clientLogin)
                    {
                        SendAndWriteLog("Processor.AppendClient method", "The manager and client can't be the same man", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Managers.IsEmpty || !_Managers.ContainsKey(managerLogin))
                    {
                        SendAndWriteLog("Processor.AppendClient method", "The manager doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Managers[managerLogin].ClientExists(clientLogin))
                    {
                        SendAndWriteLog("Processor.AppendClient method", "The client already exists.", MsgType.INFO, null);
                        return false;
                    }
                    //else if (!_Managers.ContainsKey(clientLogin))
                    //{
                    //    SendAndWriteLog("Processor.AppendClient method", "The client is manager.", MsgType.INFO);
                    //    return false;
                    //}
                    //else if (multiplier <=0 || multiplier >= 1)
                    else if (multiplier == 0)
                    {
                        SendAndWriteLog("Processor.AppendClient method", "Multiplier cannot be 0", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        DateTime systemTimeNow = SystemTimeNow();

                        _Managers[managerLogin].AppendClient(new Client(clientLogin, clientName, multiplier, systemTimeNow, systemTimeNow));
               
                        _DBAccess.AppenSubscription(managerLogin, clientLogin, clientName, multiplier, systemTimeNow);

                        string logMsg = string.Format("Client {0} ({1}) has been appended to manager {2} with multiplier {3}.", clientLogin, clientName, managerLogin, multiplier);
                        SendAndWriteLog("Processor.AppendClient method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }  
            catch (Exception e)
            {
                SendAndWriteLog("Processor.AppendClient method", e.Message, MsgType.ERROR, e);
                return false;
            }            
        }

        /// <summary>
        /// Removes a manager by his login
        /// </summary>
        /// <param name="managerLogin"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool RemoveManager(int managerLogin)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.RemoveManager method", "It's forbidden to remove managers when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Managers.IsEmpty || !_Managers.ContainsKey(managerLogin))
                    {
                        SendAndWriteLog("Processor.RemoveManager method", "The manager doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Managers[managerLogin].Clients.Count > 0)
                    {
                        SendAndWriteLog("Processor.RemoveManager method", "The manager has clients. He cannot be removed.", MsgType.INFO, null);
                        return false;
                    }
                    //else if (_Managers[managerLogin].PositionAdmin.ManagerPositionsCount() > 0)
                    //{
                    //    SendAndWriteLog("Processor.RemoveManager method", "The manager has open positions. He cannot be removed.", MsgType.INFO);
                    //    return false;
                    //}
                    //else if (_Managers[managerLogin].PositionAdmin.ClientPositionsCount() > 0)
                    //{
                    //    SendAndWriteLog("Processor.RemoveManager method", "There are open client positions. The manager cannot be removed.", MsgType.INFO);
                    //    return false;
                    //}
                    else
                    {
                        string logMsg;

                        // remove all the subscriptions by nullifying their multipliers
                        foreach (int clientLogin in _Managers[managerLogin].Clients.Keys)
                        {
                            _DBAccess.RemoveSubscription(managerLogin, clientLogin, _Managers[managerLogin].GetClient(clientLogin).Name, 0, SystemTimeNow());

                            logMsg = string.Format("Client {0} has been removed from manager {1}.", clientLogin, managerLogin);
                            SendAndWriteLog("Processor.RemoveManager method", logMsg, MsgType.INFO, null);
                        }

                        //_PositionAdmin.RemoveManager();

                        Manager removedManager;
                        if (!_Managers.TryRemove(managerLogin, out removedManager))
                        {
                            throw new Exception(string.Format("Can't remove manager {0} from the list", managerLogin));
                        }

                        _DBAccess.RemoveManager(managerLogin);

                        logMsg = string.Format("Manager {0} has been removed.", managerLogin);
                        SendAndWriteLog("Processor.RemoveManager method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }  
            catch (Exception e)
            {
                SendAndWriteLog("Processor.RemoveManager method", e.Message, MsgType.ERROR, e);
                return false;
            }           
        }

        /// <summary>
        /// Removes a subscription by the manager and client logins 
        /// </summary>
        /// <param name="managerLogin"></param>
        /// <param name="clientLogin"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool RemoveClient(int managerLogin, int clientLogin)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.RemoveClient method", "It's forbidden to remove clients when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Managers.IsEmpty || !_Managers.ContainsKey(managerLogin))
                    {
                        SendAndWriteLog("Processor.RemoveClient method", "The manager doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (!_Managers[managerLogin].ClientExists(clientLogin))
                    {
                        SendAndWriteLog("Processor.RemoveClient method", "The client doesn't exist.", MsgType.INFO, null);
                        return false;
                    }                    
                    else
                    {
                        string clientName = GetClientName(managerLogin, clientLogin);
                        _Managers[managerLogin].RemoveClient(clientLogin);
                        _DBAccess.RemoveSubscription(managerLogin, clientLogin, clientName, 0, SystemTimeNow());
                        
                        //var clientOrigTickets = _PositionAdmin.RemoveClient(clientLogin);
                        //_DBAccess.RemoveMultiClientPositions(clientOrigTickets);

                        string logMsg = string.Format("Client {0} has been removed from manager {1}.", clientLogin, managerLogin);
                        SendAndWriteLog("Processor.RemoveClient method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }  
            catch (Exception e)
            {
                SendAndWriteLog("Processor.RemoveClient method", e.Message, MsgType.ERROR, e);
                return false;
            }      
        }

        /// <summary>
        /// Updates a client multiplier 
        /// </summary>
        /// <param name="managerLogin"></param>
        /// <param name="clientLogin"></param>
        /// <param name="multiplier"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool UpdateClient(int managerLogin, int clientLogin, double multiplier)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.UpdateClient method", "It's forbidden to update clients when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else if (_Managers.IsEmpty || !_Managers.ContainsKey(managerLogin))
                    {
                        SendAndWriteLog("Processor.UpdateClient method", "The manager doesn't exist", MsgType.INFO, null);
                        return false;
                    }
                    else if (!_Managers[managerLogin].ClientExists(clientLogin))
                    {
                        SendAndWriteLog("Processor.UpdateClient method", "The client doesn't exist.", MsgType.INFO, null);
                        return false;
                    }
                    else if (multiplier == _Managers[managerLogin].GetClient(clientLogin).Multiplier)
                    {
                        SendAndWriteLog("Processor.UpdateClient method", "The new multiplier coincides with the old one.", MsgType.INFO, null);
                        return false;
                    }
                    else if (multiplier == 0)
                    {
                        SendAndWriteLog("Processor.UpdateClient method", "The multiplier cannot be 0.", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        DateTime systemTimeNow = SystemTimeNow();
                        _Managers[managerLogin].GetClient(clientLogin).ChangeMultiplier(multiplier, systemTimeNow);
                        string clientName = GetClientName(managerLogin, clientLogin);

                        _DBAccess.UpdateSubscription(managerLogin, clientLogin, clientName, multiplier, systemTimeNow);

                        string logMsg = string.Format("Client {0} has changed his multiplier for manager {1}. The new multiplier: {2}.", clientLogin, managerLogin, multiplier);
                        SendAndWriteLog("Processor.UpdateClient method", logMsg, MsgType.INFO, null);

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }  
            catch (Exception e)
            {
                SendAndWriteLog("Processor.UpdateClient method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }

        /// <summary>
        /// Gets deep clone of all the managers
        /// </summary>
        /// <returns>deep cloned managers, which can be accessed by their logins</returns>
        public Dictionary<int, Manager> GetManagers()
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    var deepClonedManagers = new Dictionary<int, Manager>();

                    foreach (Manager manager in _Managers.Values)
                    {
                        deepClonedManagers.Add(manager.Login, manager.GetDeepClone());
                    }

                    return deepClonedManagers;
                }
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.GetManagers method", e.Message, MsgType.ERROR, e);
                return null;
            }
        }

        /// <summary>
        /// Gets deep clone of a position admin
        /// </summary>
        /// <param name="managerLogin"></param>
        public PositionAdmin GetManagerPositions()
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    return _PositionAdmin.GetDeepClone();
                }
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.GetManagerPositions method", e.Message, MsgType.ERROR, e);
                return null;
            }
        }

        /// <summary>
        /// Gets a client name by his login and his manager login.
        /// </summary>
        /// <param name="managerLogin"></param>
        /// <param name="clientLogin"></param>
        /// <returns>client name or null (if the specified client is not subscribed to the specified manager)</returns>
        private string GetClientName(int managerLogin, int clientLogin)
        {
            if (_Managers.IsEmpty || !_Managers.ContainsKey(managerLogin))
            {
                return null;
            }
            else if (!_Managers[managerLogin].ClientExists(clientLogin))
            {
                return null;
            }
            else
            {
                return _Managers[managerLogin].GetClient(clientLogin).Name;
            }            
        }


        /// <summary>
        /// Refresh Multiple
        /// </summary>
        /// <param name="login"></param>
        /// <param name="name"></param>
        /// <param name="groups"></param>
        /// <param name="minBalance"></param>
        /// <returns>true if succeeded, false otherwise</returns>
        public bool RefreshMultiple(int login, string name, string password, string groups, int minBalance)
        {
            try
            {
                lock (_StateAndDBLocker)
                {
                    if (!_OnlineTradeProcessing)
                    {
                        SendAndWriteLog("Processor.RefreshMultiple method", "It's forbidden to add managers when the system is offline.", MsgType.INFO, null);
                        return false;
                    }
                    else
                    {
                        DateTime systemTimeNow = SystemTimeNow();

                        List<Account_MAM> clientsList = _DBAccessMT4.GetAccounts(login, groups, minBalance);

                        if (!_Managers.IsEmpty && _Managers.ContainsKey(login))
                        {
                            _Managers[login].RefreshMultiple(groups, minBalance);

                            _DBAccess.RefreshMultiple(login, systemTimeNow, groups, minBalance);

                            var manager = _Managers[login];

                            foreach (int clientLogin in manager.Clients.Keys)
                            {
                                var client = (clientsList != null ? clientsList.Where(it => it.Login == clientLogin).FirstOrDefault() : null);
                                if (client == null)
                                {
                                    string clientName = GetClientName(login, clientLogin);
                                    manager.RemoveClient(clientLogin);
                                    _DBAccess.RemoveSubscription(login, clientLogin, clientName, 0, SystemTimeNow());
                                }
                                else if( manager.Clients[clientLogin].Multiplier != client.Multiplier)
                                {
                                    string clientName = GetClientName(login, clientLogin);
                                    manager.GetClient(clientLogin).ChangeMultiplier(client.Multiplier, systemTimeNow);
                                    _DBAccess.UpdateSubscription(login, clientLogin, clientName, client.Multiplier, systemTimeNow);
                                }
                            }

                            if (clientsList != null)
                            {
                                clientsList.ForEach(it => {
                                    if (!manager.Clients.ContainsKey(it.Login))
                                    {
                                        manager.AppendClient(new Client(it.Login, it.Name, it.Multiplier, systemTimeNow, systemTimeNow));
                                        _DBAccess.AppenSubscription(login, it.Login, it.Name, it.Multiplier, systemTimeNow);
                                    }
                                });
                            }

                            string logMsg = string.Format("Manager {0} ({1}) has been refreshed.", login, name);
                            SendAndWriteLog("Processor.RefreshMultiple method", logMsg, MsgType.INFO, null);
                        }
                        else
                        {
                            _Managers[login] = new Manager(login, name, password, systemTimeNow, groups, minBalance);

                            _DBAccess.AppendManager(login, name, password, systemTimeNow, groups, minBalance);
                        
                            var manager = _Managers[login];

                            if (clientsList != null)
                            {
                                clientsList.ForEach(it => {
                                    manager.AppendClient(new Client(it.Login, it.Name, it.Multiplier, systemTimeNow, systemTimeNow));

                                    _DBAccess.AppenSubscription(login, it.Login, it.Name, it.Multiplier, systemTimeNow);
                                });
                            }

                            string logMsg = string.Format("Manager {0} ({1}) has been appended.", login, name);
                            SendAndWriteLog("Processor.RefreshMultiple method", logMsg, MsgType.INFO, null);
                        }

                        return true;
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                return false;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.RefreshMultiple method", e.Message, MsgType.ERROR, e);
                return false;
            }
        }





        public DateTime SystemTimeNow()
        {
            return DateTime.UtcNow.AddHours(_TimeZoneOffset);
        }

        /// <summary>
        /// Gets a user name by his login as it appears in the MT manager
        /// </summary>
        /// <param name="login"></param>
        /// <returns>user name or USER_NOT_FOUND</returns>
        public string GetMTUserName(int login)
        {
            try
            {
                string res = null;

                res = _MTManager.GetUserName(login);

                if (string.IsNullOrEmpty(res))
                {
                    res = Constants.USER_NOT_FOUND;
                }

                return res;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.GetMTUserName method", e.Message, MsgType.ERROR, e);
                return null;
            }
        }

        /// <summary>
        /// Gets a user by his login as it appears in the MT manager
        /// </summary>
        /// <param name="login"></param>
        /// <returns>UserDetails or null</returns>
        public UserDetails GetMTUser(int login)
        {
            try
            {
                return _MTManager.GetUser(login);
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.GetMTUser method", e.Message, MsgType.ERROR, e);
                return null;
            }
        }

        /// <summary>
        /// Creates log message and appends it to the log file
        /// </summary>
        /// <param name="time"></param>
        /// <param name="source"></param>
        /// <param name="msg"></param>
        /// <param name="msgType"></param>
        private void WriteLog(DateTime time, string source, string msg, MsgType msgType, Exception ex)
        {
            string logTxt = time.Format() + " " + string.Format("{0,-55}", msgType + " from " + source + ":") + "\t" + msg;
            if (msgType == MsgType.ERROR && ex != null)
            {
                logTxt += ex.StackTrace;
            }

            string _LogFile = _LogFilePath + "/MAM_" + DateTime.Today.ToString("yyyy_MM_dd") + ".log";

            try
            {
                lock (_LogFileLocker)
                {
                    using (StreamWriter logWriter = new StreamWriter(_LogFile, true))
                    {
                        logWriter.WriteLine(logTxt);
                    }
                }
            }
            catch (Exception e)
            {
                if (OnLog != null)
                {
                    OnLog.BeginInvoke(SystemTimeNow(), "WriteLog", e.Message, MsgType.ERROR, null, null);
                }
            }
        }

        /// <summary>
        /// Sends log message to GUI and appends it to the log file
        /// </summary>
        /// <param name="source"></param>
        /// <param name="msg"></param>
        /// <param name="msgType"></param>
        public void SendAndWriteLog(string source, string msg, MsgType msgType, Exception ex)
        {
            try
            {
                DateTime systemTimeNow = SystemTimeNow();

                if (OnLog != null && msgType != MsgType.INFO)
                {
                    OnLog.BeginInvoke(systemTimeNow, source, msg, msgType, null, null);
                }

                WriteLog(systemTimeNow, source, msg, msgType, ex);
            }
            catch (Exception e)
            { }
        }

        /// <summary>
        /// Processes trades offline after connection has been established/re-established
        /// </summary>
        private void ProcessTradesOffline()
        {
            int safetyIntervalInSeconds = 1; // see below how this parameter is used

            SendAndWriteLog("Processor.ProcessTradesOffline method", "Offline trade processing started.", MsgType.INFO, null);

            try
            {
                    // calculate the last min observed time
                    // we need the most recent time when the system was online
                    // so we take several known times and choose the latest
                    DateTime lastObservedTime; 
                    DateTime managerLastTradeTime = _DBAccess.GetManagerLastTradeTime();
                    DateTime clientLastTradeTime = _DBAccess.GetClientLastTradeTime();
                    lastObservedTime = clientLastTradeTime < managerLastTradeTime ? clientLastTradeTime : managerLastTradeTime;
                    var minDateTime = new DateTime(2000,1,1);

                    DateTime lastRecordedOnlineTime = GetRecordedTime();
                    lastObservedTime = lastRecordedOnlineTime < lastObservedTime || lastObservedTime < minDateTime ? lastRecordedOnlineTime : lastObservedTime;

                    ////subscription is enabled only when the system is online so it's a good indication as well
                    //DateTime subscriptionLastUpdateTime = _DBAccess.GetSubscriptionLastUpdateTime();
                    //lastObservedTime = subscriptionLastUpdateTime < lastObservedTime ? subscriptionLastUpdateTime : lastObservedTime;

                    if (lastObservedTime == Constants.DEFAULT_TIME)
                    {
                        SendAndWriteLog("Processor.ProcessTradesOffline method", string.Format("There are no previous events. Offline trade processing wasn't executed.", lastObservedTime.Format()), MsgType.INFO, null);                       
                        return;
                    }

                    // calculate the start time by subtraction the safety interval from the last observed time
                    DateTime startTime = lastObservedTime.AddSeconds(-safetyIntervalInSeconds);

                    SendAndWriteLog("Processor.ProcessTradesOffline method", string.Format("Processing trades from {0}.", startTime.Format()), MsgType.INFO, null);


                    ProcessTradesFix(startTime);


                    // manager tickets to ignore will be kept the predefined time interval from now
                    _CreatingTimeOfManagerTicketsToIgnore = SystemTimeNow();
                
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.ProcessTradesOffline method", e.Message, MsgType.ERROR, e);
            }

            SendAndWriteLog("Processor.ProcessTradesOffline method", "Offline trade processing completed.", MsgType.INFO, null);
        }

        /// <summary>
        /// Reconstructs the internal state from DB 
        /// </summary>
        private void ReconstructStateFromDB()
        {
            SendAndWriteLog("Processor.ReconstructStateFromDB method", "State reconstruction started.", MsgType.INFO, null);
            var hasError = false;

            try
            {
                lock (_StateAndDBLocker)
                {
                    // reconstruct groups            
                    _Groups = _DBAccess.LoadGroups();

                    // reconstruct groups users 
                    Dictionary<string, HashSet<Client>> clientsByGroupName = _DBAccess.LoadGroupSubscriptions();
                    foreach (string groupName in clientsByGroupName.Keys)
                    {
                        if (_Groups.IsEmpty || !_Groups.ContainsKey(groupName))
                        {
                            //throw new Exception(string.Format("Found a subscription to an unknown group: {0}.", groupName));
                            SendAndWriteLog("Processor.ReconstructStateFromDB method", string.Format("Found a subscription to an unknown group: {0}.", groupName), MsgType.ERROR, null);
                            hasError = true;
                        }
                        else
                        {
                            foreach (Client client in clientsByGroupName[groupName])
                            {
                                //if (_Groups.ContainsKey(client.Login))
                                //{
                                //    SendAndWriteLog("Processor.ReconstructStateFromDB method", string.Format("Found a subscription: {0} that already a group: {0}.", client.Login, groupName), MsgType.ERR);
                                //}
                                //else
                                //{
                                _Groups[groupName].AppendClient(client);
                                //}
                            }
                        }
                    }

                    // reconstruct managers            
                    _Managers = _DBAccess.LoadManagers();

                    // reconstruct subscriptions 
                    Dictionary<int, HashSet<Client>> clientsByManagerLogin = _DBAccess.LoadSubscriptions();
                    foreach (int managerLogin in clientsByManagerLogin.Keys)
                    {
                        if (_Managers.IsEmpty || !_Managers.ContainsKey(managerLogin))
                        {
                            //throw new Exception(string.Format("Found a subscription to an unknown manager: {0}.", managerLogin));
                            SendAndWriteLog("Processor.ReconstructStateFromDB method", string.Format("Found a subscription to an unknown manager: {0}.", managerLogin), MsgType.ERROR, null);
                            hasError = true;
                        }
                        else
                        {
                            foreach (Client client in clientsByManagerLogin[managerLogin])
                            {
                                //if (_Managers.ContainsKey(client.Login))
                                //{
                                //    SendAndWriteLog("Processor.ReconstructStateFromDB method", string.Format("Found a subscription: {0} that already a manager: {0}.", client.Login, managerLogin), MsgType.ERR);
                                //}
                                //else
                                //{
                                    _Managers[managerLogin].AppendClient(client);
                                //}
                            }
                        }
                    }

                    // reconstruct positions
                    _PositionAdmin = new PositionAdmin();
                    List<PositionInfo> managerPositions = _DBAccess.GetManagerPositions();
                    foreach (PositionInfo positionInfo in managerPositions)
                    {
                        if ((_Managers.IsEmpty || !_Managers.ContainsKey(positionInfo.Login)) && (_Groups.IsEmpty || !_Groups.ContainsKey(positionInfo.ManagerGroup)))
                        {
                            //SendAndWriteLog("Processor.ReconstructStateFromDB method", string.Format("Found a position belonging to an unknown manager: {0}.", positionInfo.Login), MsgType.ERR);
                            //hasError = true;
                            SendAndWriteLog("Processor.ReconstructStateFromDB method", string.Format("Found a position belonging to an unknown manager and group: {0} ({1}) ", positionInfo.Login, positionInfo.ManagerGroup), MsgType.INFO, null);
                        }
                        //else
                        //{
                        _PositionAdmin.AppendManagerPosition(positionInfo);
                        //}
                    }

                    // reconstruct client positions
                    Dictionary<int, List<PositionInfo>> clientPositions = _DBAccess.GetClientPositions();
                    foreach (int managerCurTicket in clientPositions.Keys)
                    {
                        var managerPositionInfo = _PositionAdmin.GetManagerPosition(managerCurTicket);

                        if (managerPositionInfo != null) // the ticket has been found 
                        {
                            foreach (PositionInfo positionInfo in clientPositions[managerCurTicket])
                            {
                                _PositionAdmin.AppendClientPosition(managerCurTicket, positionInfo);
                            }
                        }
                        else
                        {
                            SendAndWriteLog("Processor.ReconstructStateFromDB method", string.Format("Unknown manager current ticket: {0}.", managerCurTicket), MsgType.ERROR, null);
                            hasError = true;
                        }
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
                hasError = true;
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.ReconstructStateFromDB method", e.Message, MsgType.ERROR, e);
                hasError = true;
            }

            if (hasError)
            {
                SendEmailToDev("Processor.ReconstructStateFromDB method has error");
                SendSMSToDev("MAM. Reconstruct State From DB method has error.");
            }

            SendAndWriteLog("Processor.ReconstructStateFromDB method", "State reconstruction completed.", MsgType.INFO, null);
        }

        /// <summary>
        /// Records the current time if online trade processing enabled (activated by the time recording timer)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="eventArgs"></param>
        private void RecordTimeIfOnline(object sender, ElapsedEventArgs eventArgs)
        {
            if (_OnlineTradeProcessing)
            {
                string timeString = SystemTimeNow().Format();

                try
                {
                    lock (_TimeFileLocker)
                    {
                        using (StreamWriter writer = new StreamWriter(_TimeFile, false))
                        {
                            writer.WriteLine(timeString);
                        }                        
                    }
                }
                catch (Exception e)
                {
                    SendAndWriteLog("Processor.RecordTimeIfOnline method", "Unable to record time. " + e.Message, MsgType.ERROR, e);
                }
            }
        }

        /// <summary>
        /// Read the recorded time
        /// </summary>
        /// <returns>recorded time or DEFAULT_TIME (if something wrong)</returns>
        private DateTime GetRecordedTime()
        {
            string txt = null;

            try
            {
                lock (_TimeFileLocker)
                {
                    using (StreamReader reader = new StreamReader(_TimeFile))
                    {
                        txt = reader.ReadLine();
                    }
                }
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.GetRecordedTime method", "Unable to read recorded time. Default time is used instead. " + e.Message, MsgType.ERROR, e);
                return Constants.DEFAULT_TIME;
            }

            DateTime res;

            if (DateTime.TryParse(txt, out res))
            {
                if (Math.Abs((DateTime.UtcNow - res).TotalDays) < 30)
                {
                    return res;
                }
                else
                {
                    SendAndWriteLog("Processor.GetRecordedTime method", string.Format("Wrong time: {0}, parsed: {1}", txt, res.ToString("yyyy-MM-dd HH:mm")), MsgType.ERROR, null);
                    return DateTime.UtcNow.AddDays(-1);
                }
            }
            else
            {
                SendAndWriteLog("Processor.GetRecordedTime method", string.Format("Unable to parse recorded time: {0}. Default time is used instead.", txt), MsgType.ERROR, null);
                return Constants.DEFAULT_TIME;
            }               
        }

        public void ProcessTradesSync(int deltaType)
        {
            try
            {
                DateTime startTime;
                if (deltaType == 1)
                {
                    startTime = GetRecordedTime().AddMinutes(-2 * 60 * _TradesSyncInterval);
                }
                else
                {
                    SendAndWriteLog("Processor.ProcessTradesSync method", "Start Sync", MsgType.INFO, null);
                    startTime = GetRecordedTime().AddHours(-12);
                }

                ProcessTradesFix(startTime);
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.ProcessTradesOffline method", e.Message, MsgType.ERROR, e);
            }
        }

        private void ProcessTradesFix(DateTime startTime)
        {
            try
            {
                var users = new List<int>();
                var groups = string.Empty;

                lock (_StateAndDBLocker)
                {
                    // obtain all the users (both managers and clients)
                    foreach (int managerLogin in _Managers.Keys)
                    {
                        users.Add(managerLogin);

                        foreach (int clientLogin in _Managers[managerLogin].Clients.Keys)
                        {
                            if (!users.Contains(clientLogin))
                            {
                                users.Add(clientLogin);
                            }
                        }
                    }

                    // obtain all group names
                    foreach (Group group in _Groups.Values)
                    {
                        groups += (groups.Length > 0 ? "," : "") + group.Name;
                    }
                }

                // =======================================================================
                // get trades executed at or after the start time
                Dictionary<int, List<OrderInfo>> openOrderInfosByLogin = new Dictionary<int, List<OrderInfo>>();
                Dictionary<int, List<OrderInfo>> closeOrderInfosByLogin = new Dictionary<int, List<OrderInfo>>();
                SendAndWriteLog("Processor.ProcessTradesFix method", "Start Get Trades from: " + startTime.ToString("yyyy-MM-dd HH:mm"), MsgType.INFO, null);
                if (!_MTManager.GetTrades(groups, users, startTime, ref openOrderInfosByLogin, ref closeOrderInfosByLogin))
                {
                    throw new Exception("ProcessTradesFix.GetTrades method failed!");
                }

                SendAndWriteLog("Processor.ProcessTradesFix method", "End Get Trades", MsgType.INFO, null);

                ////******************** OFER **************
                //string err = "";
                //DateTime open = DateTime.MinValue;
                //int tt = _MTManager.OpenOrder(4, OrderSide.BUY, "EURUSD", 50, 1.1, "test", ref err, ref open);
                //int tt1 = _MTManager.CloseOrder(tt, OrderSide.SELL, "EURUSD", 10, 1.05, "test", true, ref err, ref open);
                //int tt2 = _MTManager.CloseOrder(tt1, OrderSide.SELL, "EURUSD", 20, 1.05, "test", true, ref err, ref open);
                //int tt3 = _MTManager.CloseOrder(tt2, OrderSide.SELL, "EURUSD", 20, 1.05, "test", true, ref err, ref open);
                ////int tt2 = _MTManager.OpenOrder(4, OrderSide.BUY, "EURUSD", 100, 1.1, "test", ref err, ref open);

                lock (_StateAndDBLocker)
                {

                    // copy trades into array and sort them according to their ticket and time 
                    // =======================================================================
                    int totalOrderInfos = 0;
                    foreach (int login in openOrderInfosByLogin.Keys)
                    {
                        totalOrderInfos += openOrderInfosByLogin[login].Count;
                    }
                    foreach (int login in closeOrderInfosByLogin.Keys)
                    {
                        totalOrderInfos += closeOrderInfosByLogin[login].Count;
                    }

                    OrderInfo[] orderInfos = new OrderInfo[totalOrderInfos];
                    bool[] close = new bool[totalOrderInfos];

                    int n = 0;

                    foreach (int login in openOrderInfosByLogin.Keys)
                    {
                        foreach (OrderInfo orderInfo in openOrderInfosByLogin[login])
                        {
                            orderInfos[n] = orderInfo;
                            close[n] = false;
                            n++;
                        }
                    }

                    foreach (int login in closeOrderInfosByLogin.Keys)
                    {
                        foreach (OrderInfo orderInfo in closeOrderInfosByLogin[login])
                        {
                            orderInfos[n] = orderInfo;
                            close[n] = true;
                            n++;
                        }
                    }

                    Array.Sort(orderInfos, close, new OrderInfoComparer());

                    // Repair volumes, if needed
                    // (if there were partial closes than the orders volumes presented in this array are incorrect and we need to repair them)
                    for (int i = orderInfos.Length - 1; i >= 0; i--) // we are iterating backward in time
                    {
                        if (close[i] && orderInfos[i].Comment.Contains("to #"))
                        {
                            // the case of partial close
                            // correct the original open volume   
                            for (int j = 0; j < orderInfos.Length; j++)
                            {
                                if (!close[j] && orderInfos[j].Ticket == orderInfos[i].Ticket)
                                {
                                    // j is the open order of i
                                    for (int k = 0; k < orderInfos.Length; k++)
                                    {
                                        if (!close[k] && orderInfos[k].Ticket == orderInfos[i].NewTicket)
                                        {
                                            // k is the open order that was created after partial close (order i);
                                            // add its volume to the volume of i
                                            orderInfos[j].Volume += orderInfos[k].Volume;
                                            break;
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                    }



                    // find in open trades ACCEPTED tickets
                    // =======================================================================
                    SendAndWriteLog("Processor.ProcessTradesFix method", "Search ACCEPTED tickets in find orders: " + orderInfos.Length, MsgType.INFO, null);
                    for (int i = 0; i < orderInfos.Length; i++)
                    {
                        if (!close[i] && !_PositionAdmin.ClientTicketExists(orderInfos[i].Ticket))
                        {
                            CheckFindAcceptedTrade(orderInfos[i]);
                        }
                    }


                    // Reset ACCEPTED tickets - IN PROCESS ticket that not opened until time limit marked as NEW
                    // =======================================================================
                    var errorList = _PositionAdmin.FindBadAcceptedClientTrade();
                    SendAndWriteLog("Processor.ProcessTradesFix method", "Find Bad Accepted client trades: " + errorList.Count, MsgType.INFO, null);
                    if (errorList != null && errorList.Count > 0)
                    {
                        errorList.ForEach(pos =>
                        {
                            var msg = string.Format("Find IN PROCESS client trade: Manager={0}, Client={1}, Symbol={2}, Side={3}, Volume={4}, Price={5}, Multiplier={6},",
                                        pos.ManagerTicket, pos.Login, pos.Symbol, pos.Side, pos.CurVolume, pos.OpenPrice, pos.Multiplier);
                            SendAndWriteLog("Processor.ProcessTradesFix method", msg, MsgType.ERROR, null);
                            pos.Status = PositionStatus.OPEN_NEW;
                            _DBAccess.UpdateOpenClientPosition(pos.ManagerTicket, pos.Login, pos.CurTicket, (int)pos.Status);
                        });
                    }

                    // find not opened client trades and open them
                    // =======================================================================
                    var ticketsToOpen = _PositionAdmin.ManagerNotFinishedOpenTickets();
                    SendAndWriteLog("Processor.ProcessTradesFix method", "Find not opened client trades: " + ticketsToOpen.Count, MsgType.INFO, null);
                    if (ticketsToOpen != null && ticketsToOpen.Count > 0)
                    {
                        foreach (var managerTicket in ticketsToOpen)
                        {
                            ProcessOpenTrade_MT4(managerTicket, 0);
                        }
                    }

                    // process the missed clients tickets 
                    // =======================================================================
                    SendAndWriteLog("Processor.ProcessTradesFix method", "Search missed closed trades in find orders:" + orderInfos.Length, MsgType.INFO, null);
                    for (int i = 0; i < orderInfos.Length; i++)
                    {
                        if (close[i])
                        {
                            if (_PositionAdmin.ClientTicketExists(orderInfos[i].Ticket))
                            {
                                ProcessCloseTrade(orderInfos[i], true, "ProcessTradesFix: Find not closed client ticket: " + orderInfos[i].Ticket);
                            }
                        }
                    }


                    // find not closed client trades and close them
                    // =======================================================================
                    var ticketsToClose = _PositionAdmin.ManagerNotFinishedCloseTickets();
                    SendAndWriteLog("Processor.ProcessTradesFix method", "find manager not finished close tickets: " + ticketsToClose.Count, MsgType.INFO, null);
                    if (ticketsToClose != null && ticketsToClose.Count > 0)
                    {
                        foreach (var managerTicket in ticketsToClose)
                        {
                            ProcessCloseTrade_MT4(managerTicket, 0);
                        }
                    }

                    // process the missed tickets
                    // =======================================================================
                    SendAndWriteLog("Processor.ProcessTradesFix method", "Search missed open trades in find orders:" + orderInfos.Length, MsgType.INFO, null);
                    var _managerTicketsToIgnore = _DBAccess.GetManagerTickets(startTime.AddMinutes(-_SafetyTimeForManagerTicketsToIgnore));

                    // process the trades (already sorted and having the correct volumes) 
                    for (int i = 0; i < orderInfos.Length; i++)
                    {
                        //TicketOwnerType tempTicketOwnerType = _Managers[tempManagerLogin].PositionAdmin.GetTicketOwnerType(ticket);
                        if (close[i])
                        {
                            if (_PositionAdmin.ManagerTicketExists(orderInfos[i].Ticket))
                            {
                                ProcessCloseTrade(orderInfos[i], true, "ProcessTradesFix: Find not closed manager ticket: " + orderInfos[i].Ticket);
                            }
                            /*
                            if (_PositionAdmin.ClientTicketExists(orderInfos[i].Ticket))
                            {
                                ProcessCloseTrade(orderInfos[i], true, "ProcessTradesFix: Find not closed client ticket: " + orderInfos[i].Ticket);
                            }*/
                        }
                        else
                        {
                            if (!_managerTicketsToIgnore.Contains(orderInfos[i].Ticket))
                            {
                                if ((!_Groups.IsEmpty && _Groups.ContainsKey(orderInfos[i].Group)) || (!_Managers.IsEmpty && _Managers.ContainsKey(orderInfos[i].Login)))
                                {
                                    ProcessOpenTrade(orderInfos[i], true, "ProcessTradesFix: Find not opened ticket: " + orderInfos[i].Ticket);
                                    _managerTicketsToIgnore.Add(orderInfos[i].Ticket);
                                }
                            }
                        }
                    }

                    SendAndWriteLog("Processor.ProcessTradesFix method", "Start process load managers and clients", MsgType.INFO, null);
                    ProcessLoadManagersAndClients();
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.ProcessTradesOffline method", e.Message, MsgType.ERROR, e);
            }
            SendAndWriteLog("Processor.ProcessTradesFix method", "End Fix", MsgType.INFO, null);
        }

        private void ProcessLoadManagersAndClients()
        {
            // get managers to append         
            var addManagersList = _DBAccess.GetManagers(ManagerStatus.NEED_APPEND);
            // add new managers
            foreach (Manager manager in addManagersList)
            {
                if(_Managers.IsEmpty || !_Managers.ContainsKey(manager.Login)){
                    AppendManager(manager.Login, manager.Name, manager.Password);
                }
            }

            #region Add / Remove Clients
            // get clients to append         
            var addClientsList = _DBAccess.GetSubscriptions(ClientStatus.NEED_APPEND);
            foreach (int managerLogin in addClientsList.Keys)
            {
                foreach (Client client in addClientsList[managerLogin])
                {
                    AppendClient(managerLogin, client.Login, client.Name, client.Multiplier);
                    _DBAccess.RemoveSubscription(managerLogin, client.Login, ClientStatus.NEED_APPEND);
                }
            }
            // get clients to update         
            var updateClientsList = _DBAccess.GetSubscriptions(ClientStatus.NEED_UPDATE);
            foreach (int managerLogin in updateClientsList.Keys)
            {
                foreach (Client client in updateClientsList[managerLogin])
                {
                    UpdateClient(managerLogin, client.Login, client.Multiplier);
                    _DBAccess.RemoveSubscription(managerLogin, client.Login, ClientStatus.NEED_UPDATE);
                }
            }
            // get clients to remove         
            var removeClientsList = _DBAccess.GetSubscriptions(ClientStatus.NEED_REMOVE);
            foreach (int managerLogin in removeClientsList.Keys)
            {
                foreach (Client client in removeClientsList[managerLogin])
                {
                    RemoveClient(managerLogin, client.Login);
                    _DBAccess.RemoveSubscription(managerLogin, client.Login, ClientStatus.NEED_REMOVE);
                }
            }
            #endregion

            // get managers to remove         
            var removeManagersList = _DBAccess.GetManagers(ManagerStatus.NEED_REMOVE);
            // remove managers
            var checkDate = new DateTime(2000,1,1);
            foreach (Manager manager in removeManagersList)
            {
                if (_Managers.IsEmpty || _Managers.ContainsKey(manager.Login))
                {
                    RemoveManager(manager.Login);
                }
            }
        }

        private void ProcessAcceptedTradesFix()
        {
            try
            {
                var users = new List<int>();

                lock (_StateAndDBLocker)
                {
                    // obtain all the clients
                    foreach (int managerLogin in _Managers.Keys)
                    {
                        foreach (int clientLogin in _Managers[managerLogin].Clients.Keys)
                        {
                            if (!users.Contains(clientLogin))
                            {
                                users.Add(clientLogin);
                            }
                        }
                    }
                }

                // =======================================================================
                // get open trades
                Dictionary<int, List<OrderInfo>> openOrderInfosByLogin = new Dictionary<int, List<OrderInfo>>();
                if (!_MTManager.GetOpenTrades(users, ref openOrderInfosByLogin))
                {
                    throw new Exception("ProcessAcceptedTradesFix.GetOpenTrades method failed!");
                }

                lock (_StateAndDBLocker)
                {
                    // find in open trades ACCEPTED tickets
                    // =======================================================================
                    foreach (List<OrderInfo> list in openOrderInfosByLogin.Values)
                    {
                        foreach (OrderInfo orderInfo in list)
                        {
                            if (!_PositionAdmin.ClientTicketExists(orderInfo.Ticket))
                            {
                                CheckFindAcceptedTrade(orderInfo);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.ProcessAcceptedTradesFix method", e.Message, MsgType.ERROR, e);
            }
        }

        public void ProcessOpenTradesForNewClient(string groupName, int clientLogin, double multiplier) 
        {
            try
            {
                if (_Groups.IsEmpty) { return; }

                SendAndWriteLog("Processor.ProcessOpenTradesForNewClient method", "Start Get Trades for group: " + groupName, MsgType.INFO, null);

                // =======================================================================
                // get trades executed at or after the start time
                Dictionary<int, List<OrderInfo>> openOrderInfosByLogin = new Dictionary<int, List<OrderInfo>>();
                if (!_MTManager.GetOpenTrades(groupName, ref openOrderInfosByLogin))
                {
                    throw new Exception("ProcessOpenTradesForNewClient.GetTrades method failed!");
                }

                SendAndWriteLog("Processor.ProcessOpenTradesForNewClient method", "End Get Trades, tickets count: " + (openOrderInfosByLogin == null ? 0 : openOrderInfosByLogin.Count), MsgType.INFO, null);

                lock (_StateAndDBLocker)
                {
                    foreach (List<OrderInfo> list in openOrderInfosByLogin.Values)
                    {
                        foreach (OrderInfo orderInfo in list)
                        {
                            if (_Groups.ContainsKey(orderInfo.Group))
                            {
                                var _groupHasClients = (_Groups[orderInfo.Group].Clients.Count > 0);

                                if (_groupHasClients)
                                {
                                    ProcessManagerOpenTradeForNewClient(orderInfo, clientLogin, multiplier);
                                }
                                else
                                {
                                    var msg1 = new StringBuilder("");
                                    if (!_groupHasClients)
                                    {
                                        msg1.Append("Group don't have clients. ");
                                    }
                                    msg1.Append("Manager=" + orderInfo.Login + ", ");
                                    msg1.Append("Group=" + orderInfo.Group + ", ");
                                    msg1.Append("Ticket=" + orderInfo.Ticket + ", ");
                                    msg1.Append("Clients=" + _Groups[orderInfo.Group].Clients.Count);

                                    SendAndWriteLog("Processor.ProcessOpenTradesForNewClient method", msg1.ToString(), MsgType.INFO, null);
                                }
                            }
                        }
                    }
                }
            }
            catch (DBAccess_Exception e)
            {
                SendAndWriteLog("DBAccess", e.Message, MsgType.ERROR, (Exception)e);
            }
            catch (Exception e)
            {
                SendAndWriteLog("Processor.ProcessOpenTradesForNewClient method", e.Message, MsgType.ERROR, e);
            }

            // find not opened client trades and open them
            // =======================================================================
            var ticketsToOpen = _PositionAdmin.ManagerNotFinishedOpenTickets();
            SendAndWriteLog("Processor.ProcessOpenTradesForNewClient method", "Start open trades: " + ticketsToOpen.Count, MsgType.INFO, null);
            if (ticketsToOpen != null && ticketsToOpen.Count > 0)
            {
                foreach (var managerTicket in ticketsToOpen)
                {
                    ProcessOpenTrade_MT4(managerTicket, 0);
                }
            }

            SendAndWriteLog("Processor.ProcessOpenTradesForNewClient method", "End Fix", MsgType.INFO, null);
        }

        private void ProcessManagerOpenTradeForNewClient(OrderInfo groupOrderInfo, int clientLogin, double multiplier)
        {
            StringBuilder msg;

            msg = new StringBuilder("Processing group manager open order. ");
            msg.Append("Manager=" + groupOrderInfo.Login + ", ");
            msg.Append("Group=" + groupOrderInfo.Group + ", ");
            msg.Append("Ticket=" + groupOrderInfo.Ticket + ", ");
            msg.Append("Symbol=" + groupOrderInfo.Symbol + ", ");
            msg.Append("Side=" + groupOrderInfo.Side + ", ");
            msg.Append("Volume=" + groupOrderInfo.Volume + ", ");
            msg.Append("Price=" + groupOrderInfo.Price + ".");

            SendAndWriteLog("Processor.ProcessManagerOpenTradeForNewClient method", msg.ToString(), MsgType.INFO, null);

            if ((OrderSide)groupOrderInfo.Side != OrderSide.BUY && (OrderSide)groupOrderInfo.Side != OrderSide.SELL)
            {
                SendAndWriteLog("Processor.ProcessGroupOpenTrade method", "Incorrect order side.", MsgType.ERROR, null);
                return;
            }

            var clientPositions = _PositionAdmin.GetClientPositions(groupOrderInfo.Ticket);
            if (clientPositions != null && clientPositions.ContainsKey(clientLogin))
            {
                msg = new StringBuilder("Client already have record for this manager ticket. ");
                msg.Append("Manager=" + groupOrderInfo.Login + ", ");
                msg.Append("Ticket=" + groupOrderInfo.Ticket + ", ");
                msg.Append("Client Login=" + clientLogin);
                SendAndWriteLog("Processor.ProcessManagerOpenTradeForNewClient method", msg.ToString(), MsgType.INFO, null);
                return;
            }

            long managerTradeID = -1;

            Group group = _Groups[groupOrderInfo.Group];

            var managerPositionInfo = _PositionAdmin.GetManagerPosition(groupOrderInfo.Ticket);

            string comment;

            if (managerPositionInfo == null)
            {
                comment = string.Format("MAM Open {0} #{1} ({2})", groupOrderInfo.Login, groupOrderInfo.Ticket, groupOrderInfo.Group);

                // append the manager position to the position admin and to DB
                managerPositionInfo = new PositionInfo(groupOrderInfo.Login,
                                                            groupOrderInfo.Group,
                                                            string.Empty,
                                                            groupOrderInfo.Ticket,
                                                            groupOrderInfo.Ticket,
                                                            groupOrderInfo.Symbol,
                                                            (OrderSide)groupOrderInfo.Side,
                                                            groupOrderInfo.Volume,
                                                            1,
                                                            groupOrderInfo.Ticket,
                                                            groupOrderInfo.Price,
                                                            comment,
                                                            PositionStatus.OPEN_NEW,
                                                            0,
                                                            0,
                                                            false,
                                                            false,
                                                            0);
                if (_PositionAdmin.AppendManagerPosition(managerPositionInfo))
                {
                    _DBAccess.AppendManagerPosition(managerPositionInfo);
                    managerTradeID = _DBAccess.AppendManagerTrade(groupOrderInfo, false, groupOrderInfo.Ticket, groupOrderInfo.Volume);
                }
            }
            else
            {
                _DBAccess.UpdateManagerPosition(groupOrderInfo.Ticket, (int)PositionStatus.OPEN_IN_PROCESS);
                managerTradeID = _DBAccess.GetManagerMaxTradeID(groupOrderInfo.Ticket);
                if (managerTradeID <= 0)
                {
                    SendAndWriteLog("Processor.ProcessManagerOpenTradeForNewClient method", "Can't resolve TradeID by Ticket = " + groupOrderInfo.Ticket, MsgType.ERROR, null);
                }
            }


            List<DBClientPositionData> clientPositionsForDB = new List<DBClientPositionData>(1);
            List<DBClientTradeData> clientTradesForDB = new List<DBClientTradeData>(1);

            string newSymbolFromManager = SymbolTransform_Manager(groupOrderInfo.Login, groupOrderInfo.Symbol);

            int clientVolume = Convert.ToInt32(Math.Floor(groupOrderInfo.Volume * multiplier));
            string symbol = groupOrderInfo.Symbol;
            Client client = group.GetClient(clientLogin);

            string newSymbolFromClient = SymbolTransform_Client(clientLogin, groupOrderInfo.Symbol);
            var newVolume = VolumeTransform_Client(clientLogin, groupOrderInfo.Symbol, clientVolume);

            if (true)
            {
                msg = new StringBuilder("Sending client open order. ");
                msg.Append("Client=" + clientLogin + ", ");
                msg.Append("Multiplier=" + multiplier + ", ");
                if (!string.IsNullOrEmpty(newSymbolFromClient))
                {
                    msg.Append("Symbol=" + newSymbolFromClient + ", ");
                    msg.Append("PrevSymbol=" + groupOrderInfo.Symbol + ", ");
                    symbol = newSymbolFromClient;
                }
                else if (!string.IsNullOrEmpty(newSymbolFromManager))
                {
                    msg.Append("Symbol=" + newSymbolFromManager + ", ");
                    msg.Append("PrevSymbol=" + groupOrderInfo.Symbol + ", ");
                    symbol = newSymbolFromManager;
                }
                else
                {
                    msg.Append("Symbol=" + symbol + ", ");
                }
                msg.Append("Side=" + (OrderSide)groupOrderInfo.Side + ", ");
                if (newVolume > 0 && newVolume != clientVolume)
                {
                    msg.Append("Volume=" + newVolume + ", ");
                    msg.Append("PrevVolume=" + clientVolume + ", ");
                    clientVolume = newVolume;
                }
                else
                {
                    msg.Append("Volume=" + clientVolume + ", ");
                }
                msg.Append("Price=" + groupOrderInfo.Price + ", ");
                msg.Append("ManagerTradeID=" + managerTradeID + ".");

                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.INFO, null);
            }

            if (clientVolume <= 0)
            {
                msg = new StringBuilder("Open order hasn't been excluded. ");
                msg.Append("Error=Ignoring Position - too small to be opened. ");
                msg.Append("Client=" + clientLogin + ", ");
                msg.Append("Multiplier=" + multiplier + ", ");
                msg.Append("Volume=" + clientVolume + ", ");
                msg.Append("ManagerTradeID=" + managerTradeID + ".");
                SendAndWriteLog("Processor.ProcessManagerOpenTrade method", msg.ToString(), MsgType.WARNING, null);
                return;
            }

            comment = string.Format("MAM Open {0} #{1} ({2})", groupOrderInfo.Login, groupOrderInfo.Ticket, group.Name);

            PositionInfo clientPositionInfo = new PositionInfo(clientLogin,
                                                                group.Name,
                                                                client.Name,
                                                                0,
                                                                0,
                                                                symbol,
                                                                (OrderSide)groupOrderInfo.Side,
                                                                clientVolume,
                                                                multiplier,
                                                                groupOrderInfo.Ticket,
                                                                groupOrderInfo.Price,
                                                                comment,
                                                                PositionStatus.OPEN_NEW,
                                                                0,
                                                                0,
                                                                false,
                                                                false,
                                                                0);
            _PositionAdmin.AppendClientPosition(groupOrderInfo.Ticket, clientPositionInfo);

            // prepare the client position for appending to DB
            clientPositionsForDB.Add(new DBClientPositionData(groupOrderInfo.Ticket, clientPositionInfo));

            // prepare the client trade for appending to DB
            OrderInfo clientOrderInfo = new OrderInfo();
            clientOrderInfo.Login = clientLogin;
            clientOrderInfo.Ticket = 0;
            clientOrderInfo.Time = SystemTimeNow();
            clientOrderInfo.Symbol = symbol;
            clientOrderInfo.Side = (OrderSide)groupOrderInfo.Side;
            clientOrderInfo.Volume = clientVolume;
            clientOrderInfo.Price = groupOrderInfo.Price;
            clientOrderInfo.Comment = comment;
            clientOrderInfo.NewTicket = Constants.NO_TICKET;
            clientTradesForDB.Add(new DBClientTradeData(clientOrderInfo, false, 0, clientVolume, managerTradeID, multiplier));

            // append all the clients' positions and trades to DB
            _DBAccess.AppendMultiClientPositions(clientPositionsForDB);
            _DBAccess.AppendMultiClientTrades(clientTradesForDB);
        }


        /// <summary>
        /// Intended for comparison of order infos by ticket, than by time
        /// </summary>
        private class OrderInfoComparer : IComparer<OrderInfo>
        {
            public int Compare(OrderInfo x, OrderInfo y)
            {
                int ticketComparisonResult = (x.Ticket).CompareTo(y.Ticket);                

                if (ticketComparisonResult != 0)
                {
                    return ticketComparisonResult;
                }
                else
                {
                    return ((DateTime)x.Time).CompareTo((DateTime)y.Time);
                }
            }
        }

        private int ParseInteger(string item)
        {
            Match match = LeadingInteger.Match(item);
            return match.Success ? int.Parse(match.Value) : 0;
        }

        public void SetPhoneNumbersForSMS(string numbers)
        {
            if (!string.IsNullOrEmpty(numbers))
            {
                phoneNumbers = numbers.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public void SetTradesSyncInterval(int interval)
        {
            _TradesSyncInterval = interval;
        }

        public void SetAcceptedTradesSyncInterval(int interval)
        {
            _AcceptedTradesSyncInterval = interval;
        }

        public void SetMaxAttempts(int maxAttempts)
        {
            _MaxAttempts = maxAttempts;
        }

        public void SetSymbolTransform_Manager_Accounts(int transformNumber, string[] list)
        {
            if (!SymbolTransform_Manager_Accounts.ContainsKey(transformNumber))
            {
                SymbolTransform_Manager_Accounts[transformNumber] = new List<int>();
            }

            int tmpInt;
            if (list == null || list.Length <= 0) { return; }

            var msg = string.Empty;
            switch (transformNumber)
            {
                case 1:
                    msg = "#1(.Z)";
                    break;
                case 2:
                    msg = "#2(Clear)";
                    break;
                default:
                    msg = "#" + transformNumber;
                    break;
            }

            foreach (var acc in list)
            {
                if (int.TryParse(acc, out tmpInt))
                {
                    SendAndWriteLog("Processor.SetSymbolTransform_Manager_Accounts", "Symbol Transform " + msg + " add manager account: " + tmpInt, MsgType.INFO, null);
                    SymbolTransform_Manager_Accounts[transformNumber].Add(tmpInt);
                }
            }
        }

        public void SetSymbolTransform_Client_Accounts(int transformNumber, string[] list)
        {
            if (!SymbolTransform_Client_Accounts.ContainsKey(transformNumber))
            {
                SymbolTransform_Client_Accounts[transformNumber] = new List<int>();
            }

            int tmpInt;
            if (list == null || list.Length <= 0) { return; }

            var msg = string.Empty;
            switch (transformNumber)
            {
                case 1:
                    msg = "#1(.Z)";
                    break;
                case 2:
                    msg = "#2(Clear)";
                    break;
                default:
                    msg = "#" + transformNumber;
                    break;
            }

            foreach (var acc in list)
            {
                if (int.TryParse(acc, out tmpInt))
                {
                    SendAndWriteLog("Processor.SetSymbolTransform_Client_Accounts", "Symbol Transform " + msg + " add client account: " + tmpInt, MsgType.INFO, null);
                    SymbolTransform_Client_Accounts[transformNumber].Add(tmpInt);
                }
            }
        }

        public string SymbolTransform_Manager(int login, string symbol)
        {
            int transformNumber = -1;
            foreach(var number in SymbolTransform_Manager_Accounts.Keys){
                if (SymbolTransform_Manager_Accounts[number].Contains(login))
                {
                    transformNumber = number;
                    break;
                }
            }

            if (transformNumber > 0)
            {
                SendAndWriteLog("Processor.SymbolTransform_Manager", string.Format("Find manager {0} that his clients need symbol transformation", login), MsgType.INFO, null);
                switch (transformNumber)
                {
                    case 1:
                        return SymbolTransform1(symbol);
                    case 2:
                        return SymbolTransform2(symbol);
                }
            }
            return null;
        }

        public string SymbolTransform_Client(int login, string symbol)
        {
            int transformNumber = -1;
            foreach (var number in SymbolTransform_Client_Accounts.Keys)
            {
                if (SymbolTransform_Client_Accounts[number].Contains(login))
                {
                    transformNumber = number;
                    break;
                }
            }

            if (transformNumber > 0)
            {
                SendAndWriteLog("Processor.SymbolTransform_Client", string.Format("Find client that need symbol transformation", login), MsgType.INFO, null);
                switch (transformNumber)
                {
                    case 1:
                        return SymbolTransform1(symbol);
                    case 2:
                        return SymbolTransform2(symbol);
                }
            }
            return null;
        }

        public string SymbolTransform1(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                return null;
            }
            SendAndWriteLog("Processor.SymbolTransform", string.Format("Make transform #1 of symbol {0}", symbol), MsgType.INFO, null);

            // If the string begins with 'XAUUSD' then replace with FT_Gold.Z
            if (symbol.StartsWith("XAUUSD", StringComparison.InvariantCultureIgnoreCase))
            {
                return "FT_Gold.Z";
            }
            // If the string begins with 'XAGUSD' then replace with FT_Silver.Z
            if (symbol.StartsWith("XAGUSD", StringComparison.InvariantCultureIgnoreCase))
            {
                return "FT_Silver.Z";
            }
            // If the string contains 'FT_DJ' then replace with 'FT_DJ30.Z'
            if (symbol.IndexOf("FT_DJ", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_DJ30.Z";
            }
            // If the string contains 'FT_Fran' then replace with 'FT_Fran40.Z'
            if (symbol.IndexOf("FT_Fran", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_Fran40.Z";
            }
            // If the string contains 'FT_S&P' then replace with 'FT_S&P500.Z'
            if (symbol.IndexOf("FT_S&P", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_S&P500.Z";
            }
            // If the string contains 'FT_NASQ' then replace with 'FT_NASQ.Z'
            if (symbol.IndexOf("FT_NASQ", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_NASQ.Z";
            }
            // If the string contains 'FT_DAX' then replace with 'FT_DAX30.Z'
            if (symbol.IndexOf("FT_DAX", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_DAX30.Z";
            }
            // If the string contains 'FT_UK' then replace with 'FT_UK100.Z'
            if (symbol.IndexOf("FT_UK", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_UK100.Z";
            }
            // If the string contains 'FT_Gold' then replace with 'FT_Gold.Z'
            if (symbol.IndexOf("FT_Gold", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_Gold.Z";
            }
            // If the string contains 'FT_Silver' then replace with 'FT_Silver.Z'
            if (symbol.IndexOf("FT_Silver", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_Silver.Z";
            }
            // If the string contains 'CL_Spot' then replace with 'CL_Spot.Z'
            if (symbol.IndexOf("CL_Spot", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "CL_Spot.Z";
            }
            // If the string contains 'SP' and 'Oil' then replace with 'CL_Spot.Z'
            if (symbol.IndexOf("SP", StringComparison.InvariantCultureIgnoreCase) >= 0 && symbol.IndexOf("Oil", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "CL_Spot.Z";
            }            
            // If one of the 2 last chars in the symbol's suffix contains the char  '.' or '_'  then return all before the '.' or '_' chars plus '.Z'
            if (symbol.Length > 2)
            {
                var index1 = symbol.LastIndexOf(".");
                var index2 = symbol.LastIndexOf("_");
                if (index1 >= symbol.Length - 2)
                {
                    return symbol.Substring(0, index1) + ".Z";
                }
                else if (index2 >= symbol.Length - 2)
                {
                    return symbol.Substring(0, index2) + ".Z";
                }
            }
            // If the string contains 6 and does not begin with the char 'X' chars then add to the symbol '.Z'
            if (symbol.Length == 6 && !symbol.StartsWith("X", StringComparison.InvariantCultureIgnoreCase))
            {
                return symbol + ".Z";
            }
            // If  the string contains 7 chars and does not contain the char '_' then replace the last char with '.Z'
            if (symbol.Length == 7 && symbol.LastIndexOf("_") < 0)
            {
                return symbol.Substring(0, symbol.Length - 1) + ".Z";
            }

            return null;
        }

        public string SymbolTransform2(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                return null;
            }
            SendAndWriteLog("Processor.SymbolTransform", string.Format("Make transform #2 of symbol {0}", symbol), MsgType.INFO, null);

            // If the string begins with 'XAUUSD' then replace with FT_Gold
            if (symbol.StartsWith("XAUUSD", StringComparison.InvariantCultureIgnoreCase))
            {
                return "FT_Gold";
            }
            // If the string begins with 'XAGUSD' then replace with FT_Silver
            if (symbol.StartsWith("XAGUSD", StringComparison.InvariantCultureIgnoreCase))
            {
                return "FT_Silver";
            }
            // If the string contains 'FT_DJ' then replace with 'FT_DJ30'
            if (symbol.IndexOf("FT_DJ", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_DJ30";
            }
            // If the string contains 'FT_Fran' then replace with 'FT_Fran40'
            if (symbol.IndexOf("FT_Fran", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_CAC40";
            }
            // If the string contains 'FT_S&P' then replace with 'FT_S&P500'
            if (symbol.IndexOf("FT_S&P", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_S&P500";
            }
            // If the string contains 'FT_NASQ' then replace with 'FT_NASQ'
            if (symbol.IndexOf("FT_NASQ", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_NASQ";
            }
            // If the string contains 'FT_DAX' then replace with 'FT_DAX30'
            if (symbol.IndexOf("FT_DAX", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_DAX30";
            }
            // If the string contains 'FT_UK' then replace with 'FT_UK100'
            if (symbol.IndexOf("FT_UK", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_UK100";
            }
            // If the string contains 'FT_Silver' then replace with 'FT_Silver'
            if (symbol.IndexOf("FT_Silver", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_Silver";
            }
            // If the string contains 'FT_Gold' then replace with 'FT_Gold'
            if (symbol.IndexOf("FT_Gold", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "FT_Gold";
            }
            // If the string contains 'CL_Spot' then replace with 'CL_Spot'
            if (symbol.IndexOf("CL_Spot", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "CL_Spot";
            }
            // If the string contains 'SP' and 'Oil' then replace with 'CL_Spot'
            if (symbol.IndexOf("SP", StringComparison.InvariantCultureIgnoreCase) >= 0 && symbol.IndexOf("Oil", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                return "CL_Spot";
            }
            // If one of the 2 last chars in the symbol's suffix contains the char  '.' or '_'  then return all before the '.' or '_' chars
            if (symbol.Length > 2)
            {
                var index1 = symbol.LastIndexOf(".");
                var index2 = symbol.LastIndexOf("_");
                if (index1 >= symbol.Length - 2)
                {
                    return symbol.Substring(0, index1);
                }
                else if (index2 >= symbol.Length - 2)
                {
                    return symbol.Substring(0, index2);
                }
            }
            // If  the string contains 7 chars and does not contain the char '_' then replace the last char with ''
            if (symbol.Length == 7 && symbol.LastIndexOf("_") < 0)
            {
                return symbol.Substring(0, symbol.Length - 1);
            }
            return null;
        }


        public void SetVolumeTransform_Client_Accounts(int transformNumber, string[] list)
        {
            if (!VolumeTransform_Client_Accounts.ContainsKey(transformNumber))
            {
                VolumeTransform_Client_Accounts[transformNumber] = new List<int>();
            }

            int tmpInt;
            if (list == null || list.Length <= 0) { return; }

            var msg = string.Empty;
            switch (transformNumber)
            {
                case 1:
                    msg = "#1(FT_Nikkei.N)";
                    break;
                default:
                    msg = "#" + transformNumber;
                    break;
            }

            foreach (var acc in list)
            {
                if (int.TryParse(acc, out tmpInt))
                {
                    SendAndWriteLog("Processor.SetVolumeTransform_Client_Accounts", "Volume Transform " + msg + " add client account: " + tmpInt, MsgType.INFO, null);
                    VolumeTransform_Client_Accounts[transformNumber].Add(tmpInt);
                }
            }
        }

        public int VolumeTransform_Client(int login, string symbol, int volume)
        {
            int transformNumber = -1;
            foreach (var number in VolumeTransform_Client_Accounts.Keys)
            {
                if (VolumeTransform_Client_Accounts[number].Contains(login))
                {
                    transformNumber = number;
                    break;
                }
            }

            if (transformNumber > 0)
            {
                SendAndWriteLog("Processor.VolumeTransform_Client", string.Format("Find client that need volume transformation", login), MsgType.INFO, null);
                switch (transformNumber)
                {
                    case 1:
                        return VolumeTransform1(symbol, volume);
                }
            }
            return 0;
        }

        public int VolumeTransform1(string symbol, int volume)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                return 0;
            }
            SendAndWriteLog("Processor.VolumeTransform", string.Format("Make transform #1 volume of symbol {0}", symbol), MsgType.INFO, null);

            // If the symbol is FT_Nikkei_N
            if (symbol.Equals("FT_Nikkei_N", StringComparison.InvariantCultureIgnoreCase))
            {
                return volume * 10;
            }

            return volume;
        }


        public void SendEmailToDev(string msg)
        {
            try
            {
                MailMessage message = new MailMessage();

                message.From = new MailAddress("info@fxglobe.com");
                message.To.Add("edward@goforex.co.il");
                message.Subject = "MAM ERROR";
                message.Body = msg;
                message.BodyEncoding = System.Text.Encoding.UTF8;
                message.IsBodyHtml = false;
                /*
                SmtpClient smtp = new SmtpClient("smtp.gmail.com", 587);
                smtp.EnableSsl = true;
                smtp.Timeout = 10000;
                smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new NetworkCredential("sunbirdfx@gmail.com", "sunbirdfx12");
                */
                SmtpClient smtp = new SmtpClient("95.138.152.111", 25);
                smtp.EnableSsl = false;
                smtp.Timeout = 10000;
                smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                smtp.UseDefaultCredentials = true;

                ServicePointManager.ServerCertificateValidationCallback = delegate(object s, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) { return true; }; 
                smtp.Send(message);
            }
            catch (Exception e)
            {
                SendAndWriteLog("can't send email", e.Message, MsgType.ERROR, e);
            }
        }

        public void SendSMSToDev(string msg)
        {
            string AccountSid = "AC9e54b631d209b8e73515bab2d60a9afd";
            string AuthToken = "2958ea2428b2ac959cfc4000f4319a1f";
            var twilio = new TwilioRestClient(AccountSid, AuthToken);

            if (phoneNumbers != null)
            {
                foreach (var phone in phoneNumbers)
                {
                    twilio.SendSmsMessage("+14156826728", phone, msg);
                }
            }
        }
    }
}
