using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
//using MT4;
using MT4ServerAPI;
using System.Collections.Concurrent;

// This file contains the simplest mam classes

namespace MAM
{
    public enum MsgType
	{
		INFO,
		ERROR,
        WARNING
	};

    public enum ConnType
    {
        PUMP,
        DIRECT
    };

    public enum ManagerStatus
    {
        DELETED = 0,
        ACTIVE = 1,
        NEED_APPEND = 2,
        NEED_REMOVE = 3
    };

    public enum ClientStatus
    {
        DELETED = 0,
        ACTIVE = 1,
        NEED_APPEND = 2,
        NEED_REMOVE = 3,
        NEED_UPDATE = 4
    };

    public enum PositionStatus
    {
        OPEN_NEW = 0,
        OPEN_IN_PROCESS = 1,
        OPEN_DONE = 9,
        CLOSE_NEW = 10,
        CLOSE_IN_PROCESS = 11,
        CLOSE_DONE_PARTIAL = 18,
        CLOSE_DONE_FULL = 19
    };

    public static class Constants
    {
        public static readonly string DEFAULT_COMMENT = string.Empty;
        public static readonly DateTime DEFAULT_TIME = DateTime.MinValue;
        public static readonly string USER_NOT_FOUND = "User not found!";
        public const int NO_TICKET = 0;
        public const int ERROR_TICKET = -1;
        public const int ACCEPTED_TICKET = -2;
        public const int NO_CONNECT = -3;
        public const int TRADE_NO_MONEY = -4;
        public const int TRADE_BAD_VOLUME = -5;
        public const int INVALID_DATA = -6;
        public const int MIN_EQUITY = 10;
    }

    /// <summary>
    /// Contains a mam-mt4 group information and his clients
    /// </summary>
    public class Group
    {
        #region "Personal data"
        public string Name { get; private set; }
        public DateTime CreateDate { get; private set; }
        #endregion

        /// <summary>
        /// holds the group managers positions and his clients' positions
        /// </summary>
        //public PositionAdmin PositionAdmin { get; private set; }

        /// <summary>
        /// information on all the clients of this group by their login
        /// </summary>
        public ConcurrentDictionary<int, Client> Clients { get; private set; } // by client login

        public int ClientsCount { get { return Clients.Count; } }

        public double TotalMultiplier { get { var m = Clients.Values.Sum(c =>  (double?)Math.Abs(c.Multiplier)); return m.HasValue ? m.Value : 0; } }

        public Group(string name, DateTime createDate)
        {
            Name = name;
            CreateDate = createDate;

            Clients = new ConcurrentDictionary<int, Client>();
        }

        public void AppendClient(Client client)
        {
            Clients.AddOrUpdate(client.Login, client, (k, v) => client);
        }

        public void RemoveClient(int clientLogin)
        {
            Client removedClient;
            if (!Clients.TryRemove(clientLogin, out removedClient))
            {
                throw new Exception(string.Format("Can't remove client {0} from the list.", clientLogin));
            }
        }

        public Client GetClient(int clientLogin)
        {
            if (Clients.ContainsKey(clientLogin))
            {
                return Clients[clientLogin];
            }
            else
            {
                return null;
            }
        }

        public bool ClientExists(int clientLogin)
        {
            if (Clients.ContainsKey(clientLogin))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Deep clones a group
        /// </summary>
        /// <returns></returns>
        public Group GetDeepClone()
        {
            Group clonedGroup = new Group(Name, CreateDate);

            foreach (Client client in Clients.Values)
            {
                if (!clonedGroup.Clients.TryAdd(client.Login, client.GetDeepClone()))
                {
                    throw new Exception(string.Format("Can't add client {0} to the list", client.Login));
                }
            }

            return clonedGroup;
        }

        public bool CheckClients(Processor processor)
        {
            return true;

            #region Not Relevant

            bool isDivideType = false;
            double _sum = 0;

            // check MAM type and calculate sum of multipliers
            foreach (Client client in Clients.Values)
            {
                if (client.Multiplier > 0 && client.Multiplier < 1)
                {
                    isDivideType = true;
                }
                _sum += client.Multiplier;
            }

            if (!isDivideType) // exit if MAM type is multiply type
            {
                return true;
            }

            bool isOK = true;
            if (_sum != 1)
            {
                processor.SendAndWriteLog("Group", string.Format("Group '{0}' - multiplier sum not equal 100%", this.Name), MsgType.WARNING, null);
                isOK = false;
            }

            //exit if not connected to MT4
            if (!processor.IsDirectConnected)
            {
                processor.SendAndWriteLog("Group", "Can't finish checking clients because no direct connection exist", MsgType.WARNING, null);
                return isOK;
            }

            /*
            var mt_group = processor.GetMTGroup(this.Name);
            if (mt_group == null)
            {
                processor.SendAndWriteLog("Group", string.Format("Group '{0}' not found in MT", this.Name), MsgType.ERR);
                return false;
            }
            */

            double sumEquity = 0;
            foreach (Client client in Clients.Values)
            {
                var mt_user = processor.GetMTUser(client.Login);
                if (mt_user == null)
                {
                    processor.SendAndWriteLog("Group", string.Format("Group '{0}' - Client #{1} not found in MT", this.Name, client.Login), MsgType.WARNING, null);
                    isOK = false;
                    continue;
                }
            }

            return isOK;

            #endregion
        }
    }

    /// <summary>
    /// Contains a mam manager information, his positions and his clients
    /// </summary>
    public class Manager
    {
        #region "Personal data"
        public int Login { get; private set; }
        public string Name { get; private set; }
        public string Password { get; private set; }
        public DateTime CreateDate { get; private set; }
        public string Groups { get; private set; }
        public int? MinBalance { get; private set; }
        #endregion

        /// <summary>
        /// holds the manager positions and his clients' positions
        /// </summary>
        //public PositionAdmin PositionAdmin { get; private set; }

        /// <summary>
        /// information on all the clients of this manager by their login
        /// </summary>
        public ConcurrentDictionary<int, Client> Clients { get; private set; } // by client login
        
        public int ClientsCount { get { return Clients.Count; } }
        
        public double TotalMultiplier { get { var m = Clients.Values.Sum(c => (double?)Math.Abs(c.Multiplier)); return m.HasValue ? m.Value : 0; } }

        public Manager(int login, string name, string password, DateTime createDate, string groups, int? minBalance)
        {
            Login = login;
            Name = name;
            Password = password;
            CreateDate = createDate;
            Groups = groups;
            MinBalance = minBalance;

            Clients = new ConcurrentDictionary<int, Client>();
        }

        public void AppendClient(Client client)
        {
            Clients.AddOrUpdate(client.Login, client, (k, v) => client);
        }

        public void RemoveClient(int clientLogin)
        {
            Client removedClient;
            if (!Clients.TryRemove(clientLogin, out removedClient))
            {
                throw new Exception(string.Format("Can't remove client {0} from the list.", clientLogin));
            }
        }

        public bool ChangePassword(string oldPassword, string newPassword)
        {
            if (oldPassword == Password)
            {
                Password = newPassword;
                return true;
            }
            else
            {
                return false;
            }
        }

        public Client GetClient(int clientLogin)
        {
            if (Clients.ContainsKey(clientLogin))
            {
                return Clients[clientLogin];
            }
            else
            {
                return null;
            }
        }

        public bool ClientExists(int clientLogin)
        {
            if (Clients.ContainsKey(clientLogin))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Deep clones a manager
        /// </summary>
        /// <returns></returns>
        public Manager GetDeepClone()
        {
            Manager clonedManager = new Manager(Login, Name, Password, CreateDate, Groups, MinBalance);

            foreach (Client client in Clients.Values)
            {
                if (!clonedManager.Clients.TryAdd(client.Login, client.GetDeepClone()))
                {
                    throw new Exception(string.Format("Can't add client {0} to the list", client.Login));
                }
            }
            
            return clonedManager;
        }

        public bool CheckClients(Processor processor)
        {
            return true;

            #region Not Relevant

            bool isDivideType = false;
            double _sum = 0;

            // check MAM type and calculate sum of multipliers
            foreach (Client client in Clients.Values)
            {
                if (client.Multiplier > 0 && client.Multiplier < 1)
                {
                    isDivideType = true;
                }
                _sum += client.Multiplier;
            }

            if (!isDivideType) // exit if MAM type is multiply type
            {
                return true;
            }

            bool isOK = true;
            if (_sum != 1)
            {
                processor.SendAndWriteLog("Manager", string.Format("Manager #{0} - multiplier sum not equal 100%", this.Login), MsgType.WARNING, null);
                isOK = false;
            }

            //exit if not connected to MT4
            if (!processor.IsDirectConnected)
            {
                processor.SendAndWriteLog("Manager", "Can't finish checking clients because no direct connection exist", MsgType.WARNING, null);
                return isOK;
            }

            var mt_manager = processor.GetMTUser(this.Login);
            if (mt_manager == null)
            {
                processor.SendAndWriteLog("Manager", string.Format("Manager #{0} not found in MT", this.Login), MsgType.WARNING, null);
                return false;
            }

            double sumEquity = 0;
            foreach (Client client in Clients.Values)
            {
                var mt_user = processor.GetMTUser(client.Login);
                if (mt_user == null)
                {
                    processor.SendAndWriteLog("Manager", string.Format("Manager #{0} - Client #{1} not found in MT", this.Login, client.Login), MsgType.WARNING, null);
                    isOK = false;
                    continue;
                }

                sumEquity += (mt_user.Balance + mt_user.Credit); 

                if (mt_user.Group != mt_manager.Group)
                {
                    processor.SendAndWriteLog("Manager", string.Format("Manager #{0} and Client #{1} group is different", this.Login, client.Login), MsgType.WARNING, null);
                    isOK = false;
                }
                if (mt_user.Leverage != mt_manager.Leverage)
                {
                    processor.SendAndWriteLog("Manager", string.Format("Manager #{0} and Client #{1} leverage is different", this.Login, client.Login), MsgType.WARNING, null);
                    isOK = false;
                }
                if (mt_user.Balance + mt_user.Credit < Constants.MIN_EQUITY)
                {
                    processor.SendAndWriteLog("Manager", string.Format("Manager #{0} - Client #{1} equity is too low", this.Login, client.Login), MsgType.WARNING, null);
                    isOK = false;
                }
            }

            if (Math.Abs(sumEquity - (mt_manager.Balance + mt_manager.Credit)) > Math.Abs(mt_manager.Balance + mt_manager.Credit) * 0.05)
            {
                processor.SendAndWriteLog("Manager", string.Format("Manager #{0} equity is not equal to sum of clients equity", this.Login), MsgType.WARNING, null);
                isOK = false;
            }
            if (mt_manager.Balance + mt_manager.Credit < Constants.MIN_EQUITY)
            {
                processor.SendAndWriteLog("Manager", string.Format("Manager #{0} equity is too low", this.Login), MsgType.WARNING, null);
                isOK = false;
            }

            return isOK;

            #endregion
        }

        public void RefreshMultiple(string groups, int? minBalance)
        {
            Groups = groups;
            MinBalance = minBalance;
        }
    }


    /// <summary>
    /// Holds the client information and his multiplier for a specific manager
    /// (client can belong to multiple managers and for each one the client can have a different multiplier).
    /// /// </summary>
    public class Client
    {        
        public int Login { get; private set; }
        public string Name { get; private set; }
        public double Multiplier { get; private set; }
        /// <summary>
        /// The last time this client multiplier was changed
        /// </summary>
        public DateTime UpdateTime { get; private set; }
        public DateTime CreateDate { get; private set; }

        /// <summary>
        /// Before creating a new client object it must be checked that the multiplier is not less than 1
        /// </summary>
        /// <param name="login"></param>
        /// <param name="multiplier"></param>
        public Client(int login, string name, double multiplier, DateTime updateTime, DateTime createDate)
        {
            Login = login;
            Name = name;
            Multiplier = multiplier;
            UpdateTime = updateTime;
            CreateDate = createDate;
        }

        public bool ChangeMultiplier(double multiplier, DateTime updateTime)
        {
            //if (multiplier >= 1)
            //{
                Multiplier = multiplier;
                UpdateTime = updateTime;
                return true;
            //}
            //else
            //{
            //    return false;
            //}
        }

        /// <summary>
        /// Deep clones the client object
        /// </summary>
        /// <returns></returns>
        public Client GetDeepClone()
        {
            Client newClonedClient = new Client(Login, Name, Multiplier, UpdateTime, CreateDate);
            return newClonedClient;
        }
    }


    /// <summary>
    /// Holds information on a specific position
    /// </summary>
    public class PositionInfo
    {
        /// <summary>
        /// the login of a client/manager, which this position belongs to
        /// </summary>
        public int Login { get; private set; }
        public string Name { get; private set; }
        public string ManagerGroup { get; private set; }
        /// <summary>
        /// the position current ticket (in a partial close this ticket may change)
        /// </summary>
        public int CurTicket { get; private set; }
        /// <summary>
        /// the position original ticket it was opened with
        /// </summary>
        public int OrigTicket { get; private set; }        
        public string Symbol { get; private set; }
        public OrderSide Side { get; private set; }
        /// <summary>
        /// The position current volume (can be different from the initial volume on partial close).
        /// This volume can equals zero in case the manager position has been closed while the clients' positions are about to be closed
        /// </summary>
        public int CurVolume { get; private set; }
        public double Multiplier { get; private set; }
        public int ManagerTicket { get; private set; }
        public double OpenPrice { get; private set; }
        public string Comment { get; private set; }
        public PositionStatus Status { get; set; }

        public DateTime CreateTime { get; set; }

        public int CloseVolume { get; set; }
        public double ClosePrice { get; private set; }
        public bool FullClose { get; set; }
        public bool CloseHedge { get; set; }
        public int Attempts { get; set; }


        public PositionInfo() {
            CreateTime = DateTime.MinValue;
        }

        public PositionInfo(int login, string group, string name, int curTicket, int origTicket, string symbol, OrderSide side, int volume, double multiplier, int managerTicket, double openPrice, string comment, PositionStatus status, int closeVolume, double closePrice, bool fullClose, bool closeHedge, int attempts)
        {
            CreateTime = DateTime.MinValue;
            Login = login;
            ManagerGroup = group;
            Name = name;
            CurTicket = curTicket;
            OrigTicket = origTicket;
            Symbol = symbol;
            Side = side;
            CurVolume = volume;
            Multiplier = multiplier;
            ManagerTicket = managerTicket;
            OpenPrice = openPrice;
            Comment = comment;
            Status = status;
            CloseVolume = closeVolume;
            ClosePrice = closePrice;
            FullClose = fullClose;
            CloseHedge = closeHedge;
            Attempts = attempts;
        }

        /// <summary>
        /// Updates the position ticket and its volume
        /// </summary>
        /// <param name="newTicket"></param>
        /// <param name="closedVolume"></param>
        public void Update(int newTicket, int closedVolume)
        {
            if (newTicket > 0)
            {
                CurTicket = newTicket;
            }
            
            CurVolume -= closedVolume;
        }

        /// <summary>
        /// Updates the position ticket and its volume
        /// </summary>
        /// <param name="newTicket"></param>
        /// <param name="closedVolume"></param>
        public void UpdateAccepted(int newTicket, double price)
        {
            if (newTicket > 0)
            {
                CurTicket = newTicket;
                OrigTicket = newTicket;
                OpenPrice = price;
                Status = PositionStatus.OPEN_DONE;
            }
        }

        /// <summary>
        /// Updates the position ticket and its volume
        /// </summary>
        /// <param name="newTicket"></param>
        /// <param name="closedVolume"></param>
        public void UpdateCreated(int newTicket, PositionStatus status)
        {
            CurTicket = newTicket;
            OrigTicket = newTicket;
            Status = status;
        }

        /// <summary>
        /// Prepare a position to the close
        /// </summary>
        /// <param name="closeVolume"></param>
        /// <param name="fullClose"></param>
        /// <param name="closeHedge"></param>
        public void UpdateToClose(int closeVolume, double closePrice, bool fullClose, bool closeHedge)
        {
            Status = PositionStatus.CLOSE_NEW;
            CloseVolume = closeVolume;
            ClosePrice = closePrice;
            FullClose = fullClose;
            CloseHedge = closeHedge;
        }

        public bool FullyClosed()
        {
            return CurVolume == 0;
        }

        /// <summary>
        /// For debugging use
        /// </summary>
        /// <returns>string representation</returns>
        public override string ToString()
        {
            return string.Format("Login={0}, Name={1}, CurTicket={2}, OrigTicket={3}, Symbol={4}, Side={5}, CurVolume={6}", Login, Name, CurTicket, OrigTicket, Symbol, Side, CurVolume);
        }
    }


    /// <summary>
    /// Holds the order information (received from the mt4 api) and the action (open/close) 
    /// </summary>
    public class TradeData
    {
        public bool Close { get; private set; }
        public OrderInfo OrderInfo { get; private set; }

        public TradeData(bool close, OrderInfo orderInfo)
        {
            Close = close;
            OrderInfo = orderInfo;
        }
    }

    /// <summary>
    /// Holds the information about a client trade that will be saved in DB 
    /// </summary>
    public class DBClientTradeData
    {
        public OrderInfo OrderInfo { get; private set; }
        public bool Close { get; private set; }
        public int OrigTicket { get; private set; }
        public int RemainingVolume { get; private set; }
        public long ManagerTradeID { get; private set; } // manager trade ID from DB table manager_trades
        public double Multiplier { get; private set; }

        public DBClientTradeData(OrderInfo orderInfo, bool close, int origTicket, int remainingVolume, long managerTradeID, double multiplier)
        {
            OrderInfo = orderInfo;
            Close = close;
            OrigTicket = origTicket;
            RemainingVolume = remainingVolume;
            ManagerTradeID = managerTradeID;
            Multiplier = multiplier;
        }
    }

    /// <summary>
    /// Holds the information about a client position that will be saved in DB 
    /// </summary>
    public class DBClientPositionData
    {
        public int ManagerCurTicket { get; private set; }
        public PositionInfo PositionInfo { get; private set; }
    
        public DBClientPositionData(int managerCurTicket, PositionInfo positionInfo)
        {
            ManagerCurTicket = managerCurTicket;
            PositionInfo = positionInfo;
        }
    }

    /*
    /// <summary>
    /// Owner - to whom this ticket belongs to
    /// </summary>
    public enum TicketOwnerType
    {
        MANAGER,
        CLIENT,
        BOTH,
        UNKNOWN
    }
    */

    public static class AuxiliaryMethods
    {           
        public static string Format(this DateTime dt)
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
