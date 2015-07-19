using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;
//using MT4;
using MT4ServerAPI;
using System.Collections.Concurrent;


namespace MAM
{
    public enum PositionSourceType
    {
        Manager = 1,
        Group = 2,
        ManagerClients = 3
    }

    public class DBAccess
    {
        // DB tables
        private const string _GROUPS = "groups";
        private const string _GROUP_SUBSCRIPTIONS = "group_clients";
        private const string _GROUP_SUBSCRIPTIONS_LOG = "group_clients_log";
        private const string _MANAGERS = "managers";
        private const string _SUBSCRIPTIONS = "subscriptions";
        private const string _SUBSCRIPTIONS_LOG = "subscriptions_log";
        private const string _MANAGER_TRADES = "manager_trades";
        private const string _CLIENT_TRADES = "client_trades";
        private const string _MANAGER_POSITIONS = "manager_positions";
        private const string _CLIENT_POSITIONS = "client_positions";

        private readonly string _DBConnectionString;
        private static readonly object _DBLocker = new object();


        public DBAccess(string connectionString)
        {
            _DBConnectionString = connectionString;
        }             

        public long AppendManagerTrade(OrderInfo orderInfo, bool close, int origTicket, int remainingVolume)
        {
            try
            {
                int action = close ? 1 : 0;
                int side;

                switch ((OrderSide)orderInfo.Side)
                {
                    case OrderSide.BUY:
                        side = 0;
                        break;
                    case OrderSide.SELL:
                        side = 1;
                        break;
                    default:
                        throw new Exception("Unknown order side.");
                }

                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _MANAGER_TRADES + " ");
                cmdTxt.Append("(Action,Ticket,Login,Time,Symbol,Side,Volume,Price,Comment,OrigTicket,RemainingVolume) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("({0},{1},{2},'{3}','{4}',{5},{6},{7},'{8}',{9},{10})");


                string cmdTxt1 = string.Format(cmdTxt.ToString(),
                                              action,
                                              orderInfo.Ticket,
                                              orderInfo.Login,
                                              ((DateTime)orderInfo.Time).Format(),
                                              orderInfo.Symbol,
                                              side,
                                              orderInfo.Volume,
                                              orderInfo.Price,
                                              orderInfo.Comment,
                                              origTicket,
                                              remainingVolume
                                             );

                long managerTradeID;

                ExecuteNonQuery(cmdTxt1);

                managerTradeID = Convert.ToInt64(ExecuteScalar(string.Format("SELECT MAX(ID) FROM {0} WHERE Ticket = {1}", _MANAGER_TRADES, orderInfo.Ticket)));
                return managerTradeID;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendManagerTrade method: " + e.Message);
            }
        }

        public long GetManagerMaxTradeID(int ticket)
        {
            long managerTradeID;
            lock (_DBLocker)
            {
                managerTradeID = Convert.ToInt64(ExecuteScalar(string.Format("SELECT MAX(ID) FROM {0} WHERE Ticket = {1}", _MANAGER_TRADES, ticket)));
            }
            return managerTradeID;
        }

        public void AppendClientTrade(OrderInfo orderInfo, bool close, int origTicket, int remainingVolume, long managerTradeID, double multiplier)
        {
            try
            {
                int action = close ? 1 : 0;
                int side;
                                                
                switch ((OrderSide)orderInfo.Side)
                {
                    case OrderSide.BUY:
                        side = 0;
                        break;
                    case OrderSide.SELL:
                        side = 1;
                        break;
                    default:
                        throw new Exception("Unknown order side.");
                }

                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _CLIENT_TRADES + " ");
                cmdTxt.Append("(Action,Ticket,Login,Time,Symbol,Side,Volume,Price,Comment,OrigTicket,RemainingVolume,ManagerTradeID,Multiplier) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("({0},{1},{2},'{3}','{4}',{5},{6},{7},'{8}',{9},{10},{11},{12})");

                string cmdTxt1 = string.Format(cmdTxt.ToString(),
                                              action,
                                              orderInfo.Ticket,
                                              orderInfo.Login,
                                              ((DateTime)orderInfo.Time).Format(),
                                              orderInfo.Symbol,
                                              side,
                                              orderInfo.Volume,
                                              orderInfo.Price,
                                              orderInfo.Comment,
                                              origTicket,
                                              remainingVolume,
                                              managerTradeID,
                                              multiplier
                                             );

                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendClientTrade method: " + e.Message);
            }
        }

        public void AppendMultiClientTrades(List<DBClientTradeData> trades)
        {
            if (trades.Count == 0)
            {
                return;
            }

            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _CLIENT_TRADES + " ");
                cmdTxt.Append("(Action,Ticket,Login,Time,Symbol,Side,Volume,Price,Comment,OrigTicket,RemainingVolume,ManagerTradeID,Multiplier) ");
                cmdTxt.Append("VALUES ");

                foreach (DBClientTradeData trade in trades)
                {                    
                    int action = trade.Close ? 1 : 0;
                    int side;

                    switch ((OrderSide)trade.OrderInfo.Side)
                    {
                        case OrderSide.BUY:
                            side = 0;
                            break;
                        case OrderSide.SELL:
                            side = 1;
                            break;
                        default:
                            throw new Exception("Unknown order side.");
                    }

                    cmdTxt.Append(string.Format("({0},{1},{2},'{3}','{4}',{5},{6},{7},'{8}',{9},{10},{11},{12}),",
                        action, trade.OrderInfo.Ticket, trade.OrderInfo.Login, ((DateTime)trade.OrderInfo.Time).Format(),
                        trade.OrderInfo.Symbol, side, trade.OrderInfo.Volume, trade.OrderInfo.Price, trade.OrderInfo.Comment,
                        trade.OrigTicket, trade.RemainingVolume, trade.ManagerTradeID, trade.Multiplier));
                }

                cmdTxt.Remove(cmdTxt.Length - 1, 1); // remove the redundant last comma

                ExecuteNonQuery(cmdTxt.ToString());
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendMultiClientTrades method: " + e.Message);
            }
        }




        public void AppendGroup(string name, DateTime createDate)
        {
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _GROUPS + " ");
                cmdTxt.Append("(`Name`,CreateDate,`Status`) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("('{0}','{1}'," + (int)ManagerStatus.ACTIVE + ") ");
                cmdTxt.Append("ON DUPLICATE KEY UPDATE CreateDate = VALUES(CreateDate), `Status` = VALUES(`Status`)");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), name, createDate.Format());
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendGroup method: " + e.Message);
            }
        }

        public void RemoveGroup(string name)
        {
            try
            {
                string cmdTxt = string.Format("DELETE FROM {0} WHERE Name = '{1}'", _GROUPS, name);
                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveGroup method: " + e.Message);
            }
        }

        public ConcurrentDictionary<string, Group> LoadGroups()
        {
            try
            {
                var groups = new ConcurrentDictionary<string, Group>();

                string cmdTxt = "SELECT `Name`, CreateDate FROM " + _GROUPS + " WHERE `Status` IN (" + (int)ManagerStatus.ACTIVE + "," + (int)ManagerStatus.NEED_REMOVE + ")";

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string groupName = Convert.ToString(reader[0]);
                                    DateTime groupCreateDate = Convert.ToDateTime(reader[1]);

                                    if (!groups.TryAdd(groupName.ToUpper(), new Group(groupName, groupCreateDate)))
                                    {
                                        throw new DBAccess_Exception("LoadGroups method: can't add a group to list.");
                                    }
                                }
                            }
                        }
                    }
                }

                return groups;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("LoadManagers method: " + e.Message);
            }
        }

        public HashSet<Group> GetGroups(ManagerStatus status)
        {
            try
            {
                var groups = new HashSet<Group>();

                string cmdTxt = "SELECT `Name`, CreateDate FROM " + _GROUPS + " WHERE `Status` = " + (int)status;

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string groupName = Convert.ToString(reader[1]);
                                    DateTime groupCreateDate = Convert.ToDateTime(reader[3]);

                                    groups.Add(new Group(groupName, groupCreateDate));
                                }
                            }
                        }
                    }
                }

                return groups;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetGroups method: " + e.Message);
            }
        }

        public void AppenGroupSubscription(string groupName, int clientLogin, string clientName, double clientMultiplier, DateTime updateTime)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _GROUP_SUBSCRIPTIONS + " ");
                cmdTxt.Append("(GroupName,ClientLogin,ClientName,ClientMultiplier,CreateDate,UpdateTime,`Status`) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("('{0}',{1},'{2}',{3},'{4}','{4}',{5})");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), groupName, clientLogin, clientName, clientMultiplier, updateTime.Format(), (int)ClientStatus.ACTIVE);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendSubscription method: " + e.Message);
            }
        }

        public void UpdateGroupSubscription(string groupName, int clientLogin, string clientName, double clientMultiplier, DateTime updateTime)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _GROUP_SUBSCRIPTIONS_LOG + " ");
                cmdTxt.Append("(GroupName,ClientLogin,ClientName,ClientMultiplier,CreateDate,UpdateTime,`Status`) ");
                cmdTxt.Append("SELECT GroupName, ClientLogin, ClientName, ClientMultiplier, CreateDate, UpdateTime, `Status` ");
                cmdTxt.Append("FROM " + _GROUP_SUBSCRIPTIONS + " WHERE GroupName = '{0}' AND ClientLogin = {1} ");
                cmdTxt.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ") ");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), groupName, clientLogin);
                ExecuteNonQuery(cmdTxt1);

                cmdTxt = new StringBuilder();
                cmdTxt.Append("UPDATE " + _GROUP_SUBSCRIPTIONS + " ");
                cmdTxt.Append("SET ClientMultiplier = {2}, UpdateTime = '{3}', `Status` = {4} ");
                cmdTxt.Append("WHERE GroupName = '{0}' AND ClientLogin = {1} ");
                cmdTxt.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ") ");

                cmdTxt1 = string.Format(cmdTxt.ToString(), groupName, clientLogin, clientMultiplier, updateTime.Format(), (int)ClientStatus.ACTIVE);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateGroupSubscription method: " + e.Message);
            }
        }

        public void RemoveGroupSubscription(string groupName, int clientLogin, string clientName, double clientMultiplier, DateTime updateTime)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _GROUP_SUBSCRIPTIONS_LOG + " ");
                cmdTxt.Append("(GroupName,ClientLogin,ClientName,ClientMultiplier,CreateDate,UpdateTime,`Status`) ");
                cmdTxt.Append("SELECT GroupName, ClientLogin, ClientName, ClientMultiplier, CreateDate, UpdateTime, `Status` ");
                cmdTxt.Append("FROM " + _GROUP_SUBSCRIPTIONS + " WHERE GroupName = '{0}' AND ClientLogin = {1} ");
                cmdTxt.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ',' + (int)ClientStatus.DELETED + ") ");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), groupName, clientLogin);
                ExecuteNonQuery(cmdTxt1);

                StringBuilder cmdTxt2 = new StringBuilder();
                cmdTxt2.Append("UPDATE " + _GROUP_SUBSCRIPTIONS + " ");
                cmdTxt2.Append("SET ClientMultiplier = {2}, UpdateTime = '{3}', `Status` = {4} ");
                cmdTxt2.Append("WHERE GroupName = '{0}' AND ClientLogin = {1} ");
                cmdTxt2.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ") ");

                cmdTxt1 = string.Format(cmdTxt2.ToString(), groupName, clientLogin, 0, updateTime.Format(), (int)ClientStatus.DELETED);
                ExecuteNonQuery(cmdTxt1);

                cmdTxt1 = string.Format(cmdTxt.ToString(), groupName, clientLogin);
                ExecuteNonQuery(cmdTxt1);

                cmdTxt1 = string.Format("DELETE FROM " + _GROUP_SUBSCRIPTIONS + " WHERE GroupName = '{0}' AND ClientLogin = {1} AND `Status` = {2}", groupName, clientLogin, (int)ClientStatus.DELETED);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveGroupSubscription method: " + e.Message);
            }
        }

        public void RemoveGroupSubscription(string groupName, int clientLogin, ClientStatus status)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                string cmdTxt = string.Format("DELETE FROM " + _GROUP_SUBSCRIPTIONS + " WHERE GroupName = '{0}' AND ClientLogin = {1} AND `Status` = {2}", groupName, clientLogin, (int)status);
                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveGroupSubscription method: " + e.Message);
            }
        }

        public Dictionary<string, HashSet<Client>> LoadGroupSubscriptions()
        {
            try
            {
                Dictionary<string, HashSet<Client>> subscriptions = new Dictionary<string, HashSet<Client>>();

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        var cmdTxt = "SELECT GroupName, ClientLogin, ClientName, ClientMultiplier, UpdateTime, CreateDate FROM " + _GROUP_SUBSCRIPTIONS + " WHERE `Status` IN (" + (int)ClientStatus.ACTIVE + ") ";

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string groupName = Convert.ToString(reader[0]).ToUpper();
                                    int clientLogin = Convert.ToInt32(reader[1]);
                                    string clientName = Convert.ToString(reader[2]);
                                    double clientMultiplier = Convert.ToDouble(reader[3]);
                                    DateTime updateTime = Convert.ToDateTime(reader[4]);
                                    DateTime createDate = Convert.ToDateTime(reader[5]);

                                    if (!subscriptions.ContainsKey(groupName))
                                    {
                                        subscriptions[groupName] = new HashSet<Client>();
                                    }

                                    subscriptions[groupName].Add(new Client(clientLogin, clientName, clientMultiplier, updateTime, createDate));
                                }
                            }
                        }
                    }
                }

                return subscriptions;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("LoadGroupSubscriptions method: " + e.Message);
            }
        }

        public Dictionary<string, HashSet<Client>> GetGroupSubscriptions(ClientStatus status)
        {
            try
            {
                var subscriptions = new Dictionary<string, HashSet<Client>>();

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        // return last (current or request) status

                        StringBuilder cmdTxt = new StringBuilder();
                        cmdTxt.Append("SELECT GroupName, ClientLogin, ClientName, ClientMultiplier, UpdateTime, CreateDate FROM " + _GROUP_SUBSCRIPTIONS + " WHERE `Status` IN (" + (int)status + ") ");

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt.ToString(), conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string groupName = Convert.ToString(reader[0]).ToUpper();
                                    int clientLogin = Convert.ToInt32(reader[1]);
                                    string clientName = Convert.ToString(reader[2]);
                                    double clientMultiplier = Convert.ToDouble(reader[3]);
                                    DateTime updateTime = Convert.ToDateTime(reader[4]);
                                    DateTime createDate = Convert.ToDateTime(reader[5]);

                                    if (!subscriptions.ContainsKey(groupName))
                                    {
                                        subscriptions[groupName] = new HashSet<Client>();
                                    }

                                    subscriptions[groupName].Add(new Client(clientLogin, clientName, clientMultiplier, updateTime, createDate));
                                }
                            }
                        }
                    }
                }

                return subscriptions;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetGroupSubscriptions method: " + e.Message);
            }
        }




        public void AppendManager(int login, string name, string password, DateTime createDate)
        {
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _MANAGERS + " ");
                cmdTxt.Append("(Login,`Name`,Password,CreateDate,`Status`) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("({0},'{1}','{2}','{3}'," + (int)ManagerStatus.ACTIVE + ") ");
                cmdTxt.Append("ON DUPLICATE KEY UPDATE CreateDate = VALUES(CreateDate), `Status` = VALUES(`Status`)");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), login, name, password, createDate.Format());
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendManager method: " + e.Message);
            }
        }

        public void AppendManager(int login, string name, string password, DateTime createDate, string groups, int minBalance)
        {
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _MANAGERS + " ");
                cmdTxt.Append("(Login,`Name`,Password,CreateDate,`Status`,Groups,MinBalance) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("({0},'{1}','{2}','{3}'," + (int)ManagerStatus.ACTIVE + ",'{4}',{5}) ");
                cmdTxt.Append("ON DUPLICATE KEY UPDATE CreateDate = VALUES(CreateDate), `Status` = VALUES(`Status`)");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), login, name, password, createDate.Format(), groups, minBalance);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendManager method: " + e.Message);
            }
        }

        public void RefreshMultiple(int login, DateTime createDate, string groups, int minBalance)
        {
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("UPDATE " + _MANAGERS + " ");
                cmdTxt.Append("SET CreateDate = '{1}', Groups = '{2}', MinBalance = {3} ");
                cmdTxt.Append("WHERE Login = {0}");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), login, createDate.Format(), groups, minBalance);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateManager method: " + e.Message);
            }
        }

        public void RemoveManager(int managerLogin)
        {
            try
            {
                string cmdTxt1 = string.Format("DELETE FROM {0} WHERE Login = {1}", _MANAGER_POSITIONS, managerLogin);
                ExecuteNonQuery(cmdTxt1);
                string cmdTxt2 = string.Format("DELETE FROM {0} WHERE Login = {1}", _MANAGERS, managerLogin);
                ExecuteNonQuery(cmdTxt2);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveManager method: " + e.Message);
            }
        }

        public ConcurrentDictionary<int, Manager> LoadManagers()
        {
            try
            {
                var managers = new ConcurrentDictionary<int, Manager>();

                string cmdTxt = "SELECT Login, `Name`, Password, CreateDate, Groups, MinBalance FROM " + _MANAGERS + " WHERE `Status` IN (" + (int)ManagerStatus.ACTIVE + "," + (int)ManagerStatus.NEED_REMOVE + ")";

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int managerLogin = Convert.ToInt32(reader[0]);
                                    string managerName = Convert.ToString(reader[1]);
                                    string managerPassword = Convert.ToString(reader[2]);
                                    DateTime managerCreateDate = Convert.ToDateTime(reader[3]);
                                    string groups = Convert.ToString(reader[4]);
                                    int? minBalance = (string.IsNullOrEmpty(Convert.ToString(reader[5])) ? (int?)null : Convert.ToInt32(reader[5]));

                                    if (!managers.TryAdd(managerLogin, new Manager(managerLogin, managerName, managerPassword, managerCreateDate, groups, minBalance)))
                                    {
                                        throw new DBAccess_Exception("LoadManagers method: can't add a manager to list.");
                                    }
                                }
                            }
                        }
                    }
                }

                return managers;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("LoadManagers method: " + e.Message);
            }
        }

        public HashSet<Manager> GetManagers(ManagerStatus status)
        {
            try
            {
                var managers = new HashSet<Manager>();

                string cmdTxt = "SELECT Login, `Name`, Password, CreateDate, Groups, MinBalance FROM " + _MANAGERS + " WHERE `Status` = " + (int)status;

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int managerLogin = Convert.ToInt32(reader[0]);
                                    string managerName = Convert.ToString(reader[1]);
                                    string managerPassword = Convert.ToString(reader[2]);
                                    DateTime managerCreateDate = Convert.ToDateTime(reader[3]);
                                    string groups = Convert.ToString(reader[4]);
                                    int? minBalance = (int?)reader[5];

                                    managers.Add(new Manager(managerLogin, managerName, managerPassword, managerCreateDate, groups, minBalance));
                                }
                            }
                        }
                    }
                }

                return managers;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetManagers method: " + e.Message);
            }
        }

        public string GetManagerPassword(int login)
        {
            try
            {
                string cmdTxt = string.Format("SELECT Password FROM {0} WHERE Login = {1}  WHERE `Status` IN (" + (int)ManagerStatus.ACTIVE + "," + (int)ManagerStatus.NEED_REMOVE + ")", _MANAGERS, login);
                return Convert.ToString(ExecuteScalar(cmdTxt));
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetManagerPassword method: " + e.Message);
            }
        }

        public bool ChangeManagerPassword(int login, string oldPassword, string newPassword)
        {
            try
            {
                string actualOldPassword = GetManagerPassword(login);

                if (actualOldPassword == oldPassword)
                {
                    string cmdTxt = string.Format("UPDATE {0} SET Password = '{1}' WHERE Login = {2}", _MANAGERS, newPassword, login);
                    ExecuteNonQuery(cmdTxt.ToString());

                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("ChangeManagerPassword method: " + e.Message);
            }
        }

        public void AppenSubscription(int managerLogin, int clientLogin, string clientName, double clientMultiplier, DateTime updateTime)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _SUBSCRIPTIONS + " ");
                cmdTxt.Append("(ManagerLogin,ClientLogin,ClientName,ClientMultiplier,CreateDate,UpdateTime,`Status`) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("({0},{1},'{2}',{3},'{4}','{4}',{5})");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), managerLogin, clientLogin, clientName, clientMultiplier, updateTime.Format(), (int)ClientStatus.ACTIVE);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendSubscription method: " + e.Message);
            }
        }

        public void UpdateSubscription(int managerLogin, int clientLogin, string clientName, double clientMultiplier, DateTime updateTime)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _SUBSCRIPTIONS_LOG + " ");
                cmdTxt.Append("(ManagerLogin,ClientLogin,ClientName,ClientMultiplier,CreateDate,UpdateTime,`Status`) ");
                cmdTxt.Append("SELECT ManagerLogin, ClientLogin, ClientName, ClientMultiplier, CreateDate, UpdateTime, `Status` ");
                cmdTxt.Append("FROM " + _SUBSCRIPTIONS + " WHERE ManagerLogin = {0} AND ClientLogin = {1} ");
                cmdTxt.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ") ");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), managerLogin, clientLogin);
                ExecuteNonQuery(cmdTxt1);

                cmdTxt = new StringBuilder();
                cmdTxt.Append("UPDATE " + _SUBSCRIPTIONS + " ");
                cmdTxt.Append("SET ClientMultiplier = {2}, UpdateTime = '{3}', `Status` = {4} ");
                cmdTxt.Append("WHERE ManagerLogin = {0} AND ClientLogin = {1} ");
                cmdTxt.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ") ");

                cmdTxt1 = string.Format(cmdTxt.ToString(), managerLogin, clientLogin, clientMultiplier, updateTime.Format(), (int)ClientStatus.ACTIVE);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateSubscription method: " + e.Message);
            }
        }

        public void RemoveSubscription(int managerLogin, int clientLogin, string clientName, double clientMultiplier, DateTime updateTime)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _SUBSCRIPTIONS_LOG + " ");
                cmdTxt.Append("(ManagerLogin,ClientLogin,ClientName,ClientMultiplier,CreateDate,UpdateTime,`Status`) ");
                cmdTxt.Append("SELECT ManagerLogin, ClientLogin, ClientName, ClientMultiplier, CreateDate, UpdateTime, `Status` ");
                cmdTxt.Append("FROM " + _SUBSCRIPTIONS + " WHERE ManagerLogin = {0} AND ClientLogin = {1} ");
                cmdTxt.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ',' + (int)ClientStatus.DELETED + ") ");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), managerLogin, clientLogin);
                ExecuteNonQuery(cmdTxt1);

                StringBuilder cmdTxt2 = new StringBuilder();
                cmdTxt2.Append("UPDATE " + _SUBSCRIPTIONS + " ");
                cmdTxt2.Append("SET ClientMultiplier = {2}, UpdateTime = '{3}', `Status` = {4} ");
                cmdTxt2.Append("WHERE ManagerLogin = {0} AND ClientLogin = {1} ");
                cmdTxt2.Append("AND `Status` IN (" + (int)ClientStatus.ACTIVE + ") ");

                cmdTxt1 = string.Format(cmdTxt2.ToString(), managerLogin, clientLogin, 0, updateTime.Format(), (int)ClientStatus.DELETED);
                ExecuteNonQuery(cmdTxt1);

                cmdTxt1 = string.Format(cmdTxt.ToString(), managerLogin, clientLogin);
                ExecuteNonQuery(cmdTxt1);

                cmdTxt1 = string.Format("DELETE FROM " + _SUBSCRIPTIONS + " WHERE ManagerLogin = {0} AND ClientLogin = {1} AND `Status` = {2}", managerLogin, clientLogin, (int)ClientStatus.DELETED);
                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveSubscription method: " + e.Message);
            }
        }

        public void RemoveSubscription(int managerLogin, int clientLogin, ClientStatus status)
        {
            // ClientMultiplier equal 0 indicates a canceled subscription
            try
            {
                string cmdTxt = string.Format("DELETE FROM " + _SUBSCRIPTIONS + " WHERE ManagerLogin = {0} AND ClientLogin = {1} AND `Status` = {2}", managerLogin, clientLogin, (int)status);
                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveSubscription method: " + e.Message);
            }
        }

        public Dictionary<int, HashSet<Client>> LoadSubscriptions()
        {
            try
            {
                Dictionary<int, HashSet<Client>> subscriptions = new Dictionary<int, HashSet<Client>>();

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        var cmdTxt = "SELECT ManagerLogin, ClientLogin, ClientName, ClientMultiplier, UpdateTime, CreateDate FROM " + _SUBSCRIPTIONS + " WHERE `Status` IN (" + (int)ClientStatus.ACTIVE + ") ";

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int managerLogin = Convert.ToInt32(reader[0]);
                                    int clientLogin = Convert.ToInt32(reader[1]);
                                    string clientName = Convert.ToString(reader[2]);
                                    double clientMultiplier = Convert.ToDouble(reader[3]);
                                    DateTime updateTime = Convert.ToDateTime(reader[4]);
                                    DateTime createDate = Convert.ToDateTime(reader[5]);

                                    if (!subscriptions.ContainsKey(managerLogin))
                                    {
                                        subscriptions[managerLogin] = new HashSet<Client>();
                                    }

                                    subscriptions[managerLogin].Add(new Client(clientLogin, clientName, clientMultiplier, updateTime, createDate));
                                }
                            }
                        }
                    }
                }

                return subscriptions;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("LoadSubscriptions method: " + e.Message);
            }
        }

        public Dictionary<int, HashSet<Client>> GetSubscriptions(ClientStatus status)
        {
            try
            {
                var subscriptions = new Dictionary<int, HashSet<Client>>();

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        // return last (current or request) status

                        //StringBuilder cmdTxt = new StringBuilder();
                        //cmdTxt.Append("SELECT sub.ManagerLogin, sub.ClientLogin, sub.ClientName, sub.ClientMultiplier, sub.UpdateTime, sub.CreateDate FROM " + _SUBSCRIPTIONS + " sub ");
                        //cmdTxt.Append("INNER JOIN ");
                        //cmdTxt.Append("(SELECT ManagerLogin, ClientLogin, MAX(UpdateTime) AS LastUpdateTime, MIN(UpdateTime) AS CreateDate ");
                        //cmdTxt.Append(" FROM " + _SUBSCRIPTIONS + " GROUP BY ManagerLogin, ClientLogin) AS temp ");
                        //cmdTxt.Append("ON sub.ManagerLogin = temp.ManagerLogin AND sub.ClientLogin = temp.ClientLogin ");
                        //cmdTxt.Append("WHERE sub.UpdateTime = temp.LastUpdateTime AND sub.Status IN (" + (int)status + ")");
                        StringBuilder cmdTxt = new StringBuilder();
                        cmdTxt.Append("SELECT ManagerLogin, ClientLogin, ClientName, ClientMultiplier, UpdateTime, CreateDate FROM " + _SUBSCRIPTIONS + " WHERE `Status` IN (" + (int)status + ") ");

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt.ToString(), conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int managerLogin = Convert.ToInt32(reader[0]);
                                    int clientLogin = Convert.ToInt32(reader[1]);
                                    string clientName = Convert.ToString(reader[2]);
                                    double clientMultiplier = Convert.ToDouble(reader[3]);
                                    DateTime updateTime = Convert.ToDateTime(reader[4]);
                                    DateTime createDate = Convert.ToDateTime(reader[5]);

                                    if (!subscriptions.ContainsKey(managerLogin))
                                    {
                                        subscriptions[managerLogin] = new HashSet<Client>();
                                    }

                                    subscriptions[managerLogin].Add(new Client(clientLogin, clientName, clientMultiplier, updateTime, createDate));
                                }
                            }
                        }
                    }
                }

                return subscriptions;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetSubscriptions method: " + e.Message);
            }
        }

        public HashSet<Client> GetSubscribersByManagerLogin(int managerLogin)
        {
            try
            {
                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("SELECT ClientLogin, ClientName, ClientMultiplier, UpdateTime, CreateDate FROM " + _SUBSCRIPTIONS + " WHERE `Status` IN (" + (int)ClientStatus.ACTIVE + ") ");
                cmdTxt.Append("AND ManagerLogin = {0}");

                string cmdTxt1 = string.Format(cmdTxt.ToString(), managerLogin);

                HashSet<Client> subscribers = new HashSet<Client>();
                
                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt1, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int clientLogin = Convert.ToInt32(reader[0]);
                                    string clientName = Convert.ToString(reader[1]);
                                    double clientMultiplier = Convert.ToDouble(reader[2]);
                                    DateTime updateTime = Convert.ToDateTime(reader[3]);
                                    DateTime createDate = Convert.ToDateTime(reader[4]);

                                    subscribers.Add(new Client(clientLogin, clientName, clientMultiplier, updateTime, createDate));
                                }
                            }
                        }
                    }
                }

                return subscribers;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetSubscribersByManagerLogin method: " + e.Message);
            }
        }

        public double GetMultiplier(int managerLogin, int clientLogin)
        {
            try
            {
                //string cmdTxt = string.Format("SELECT ClientMultiplier FROM {0} WHERE ManagerLogin = {1} AND ClientLogin = {2} AND `Status` = " + (int)ClientStatus.ACTIVE + " ORDER BY UpdateTime DESC LIMIT 1", _SUBSCRIPTIONS, managerLogin, clientLogin);
                string cmdTxt = string.Format("SELECT ClientMultiplier FROM {0} WHERE ManagerLogin = {1} AND ClientLogin = {2} AND `Status` = {3}", _SUBSCRIPTIONS, managerLogin, clientLogin, (int)ClientStatus.ACTIVE);

                return Convert.ToDouble(ExecuteScalar(cmdTxt));
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetMultiplier method: " + e.Message);
            }
        }

        public DateTime GetSubscriptionLastUpdateTime()
        {
            try
            {
                string cmdTxt = "SELECT MAX(UpdateTime) FROM " + _SUBSCRIPTIONS;

                object queryRes = ExecuteScalar(cmdTxt);

                if (queryRes.Equals(System.DBNull.Value))
                {
                    return Constants.DEFAULT_TIME;
                }
                else
                {
                    return Convert.ToDateTime(queryRes);
                }
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetSubscriptionLastUpdateTime method: " + e.Message);
            }
        }

        public void AppendManagerPosition(PositionInfo position)
        {
            try
            {
                int side;

                switch (position.Side)
                {
                    case OrderSide.BUY:
                        side = 0;
                        break;
                    case OrderSide.SELL:
                        side = 1;
                        break;
                    default:
                        throw new Exception("Unknown position side.");
                }

                StringBuilder cmdTxt = new StringBuilder();
                cmdTxt.Append("INSERT INTO " + _MANAGER_POSITIONS + " ");
                cmdTxt.Append("(CurTicket,Login,`Group`,`Name`,OrigTicket,Symbol,Side,CurVolume,`Status`) ");
                cmdTxt.Append("VALUES ");
                cmdTxt.Append("({0},{1},'{2}','{3}',{4},'{5}',{6},{7},{8})");

                string cmdTxt1 = string.Format(cmdTxt.ToString(),
                    position.CurTicket, position.Login, position.ManagerGroup, position.Name, position.OrigTicket,
                    position.Symbol, side, position.CurVolume, (int)position.Status);

                ExecuteNonQuery(cmdTxt1);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendManagerPosition method: " + e.Message);
            }
        }

        public void RemoveManagerPosition(int origTicket)
        {
            try
            {
                string cmdTxt = string.Format("DELETE FROM " + _MANAGER_POSITIONS + " WHERE OrigTicket = {0}", origTicket);

                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveManagerPosition method: " + e.Message);
            }
        }

        public List<PositionInfo> GetManagerPositions()
        {
            try
            {
                List<PositionInfo> res = new List<PositionInfo>();

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        StringBuilder cmdTxt = new StringBuilder();
                        cmdTxt.Append("SELECT all_pos.Login, all_pos.`Group`, all_pos.`Name`, all_pos.CurTicket, ");
                        cmdTxt.Append("all_pos.OrigTicket, all_pos.Symbol, all_pos.Side, all_pos.CurVolume, all_pos.OpenPrice, all_pos.`Status` ");
                        cmdTxt.Append("FROM " + _MANAGER_POSITIONS + " all_pos ");
                        cmdTxt.Append("INNER JOIN ");
                        cmdTxt.Append("(SELECT MAX(ID) AS ID FROM " + _MANAGER_POSITIONS + " GROUP BY Login, OrigTicket) AS cur_pos ");
                        cmdTxt.Append("ON all_pos.ID = cur_pos.ID");

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt.ToString(), conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int login = Convert.ToInt32(reader[0]);
                                    string group = Convert.ToString(reader[1]);
                                    string name = Convert.ToString(reader[2]);
                                    int curTicket = Convert.ToInt32(reader[3]);
                                    int origTicket = Convert.ToInt32(reader[4]);
                                    string symbol = Convert.ToString(reader[5]);
                                    int sideCode = Convert.ToInt32(reader[6]);
                                    int curVolume = Convert.ToInt32(reader[7]);
                                    double openPrice = Convert.ToDouble(reader[8]);
                                    int status = Convert.ToInt32(reader[9]);

                                    OrderSide side;

                                    switch (sideCode)
                                    {
                                        case 0:
                                            side = OrderSide.BUY;
                                            break;
                                        case 1:
                                            side = OrderSide.SELL;
                                            break;
                                        default:
                                            side = OrderSide.UNKNOWN;
                                            break;
                                    }

                                    res.Add(new PositionInfo(login, group, name, curTicket, origTicket, symbol, side, curVolume, 1, curTicket, openPrice, string.Empty, (PositionStatus)status, 0, 0, false, false, 0));
                                }
                            }
                        }
                    }
                }

                return res;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetManagerPositions method: " + e.Message);
            }
        }

        public void UpdateManagerPosition(int curTicket, int status)
        {
            try
            {
                string cmdTxt = string.Format("UPDATE " + _MANAGER_POSITIONS + " SET `Status` = {0} WHERE CurTicket = {1}", status, curTicket);

                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateManagerPosition method: " + e.Message);
            }
        }

        public void AppendMultiClientPositions(List<DBClientPositionData> positions)
        {
            if (positions.Count == 0)
            {
                return;
            }

            StringBuilder cmdTxt = new StringBuilder("");
            try
            {
                cmdTxt.Append("INSERT INTO " + _CLIENT_POSITIONS + " ");
                cmdTxt.Append("(CurTicket,Login,`Name`,OrigTicket,Symbol,Side,CurVolume,Multiplier,ManagerCurTicket,OpenPrice,Comment,`Status`,CloseVolume,ClosePrice,FullClose,CloseHedge) ");
                cmdTxt.Append("VALUES ");

                foreach (DBClientPositionData pos in positions)
                {
                    int side;

                    switch (pos.PositionInfo.Side)
                    {
                        case OrderSide.BUY:
                            side = 0;
                            break;
                        case OrderSide.SELL:
                            side = 1;
                            break;
                        default:
                            throw new Exception("Unknown position side.");
                    }

                    //var curTicket = pos.PositionInfo.CurTicket == Constants.ACCEPTED_TICKET ? 0 : pos.PositionInfo.CurTicket;
                    //var origTicket = pos.PositionInfo.OrigTicket == Constants.ACCEPTED_TICKET ? 0 : pos.PositionInfo.OrigTicket;
                    var curTicket = pos.PositionInfo.CurTicket;
                    var origTicket = pos.PositionInfo.OrigTicket;

                    cmdTxt.Append(string.Format("({0},{1},'{2}',{3},'{4}',{5},{6},{7},{8},{9},'{10}',{11},{12},{13},{14},{15}),",
                        curTicket, pos.PositionInfo.Login, pos.PositionInfo.Name, origTicket,
                        pos.PositionInfo.Symbol, side, pos.PositionInfo.CurVolume, pos.PositionInfo.Multiplier, pos.ManagerCurTicket,
                        pos.PositionInfo.OpenPrice, pos.PositionInfo.Comment, (int)pos.PositionInfo.Status,
                        pos.PositionInfo.CloseVolume, pos.PositionInfo.ClosePrice, pos.PositionInfo.FullClose ? 1 : 0, pos.PositionInfo.CloseHedge ? 1 : 0));
                }

                cmdTxt.Remove(cmdTxt.Length - 1, 1); // remove the redundant last comma

                ExecuteNonQuery(cmdTxt.ToString());
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("AppendMultiClientPositions method: " + e.Message + " data: " + cmdTxt.ToString());
            }
        }

        public void RemoveClientPosition(int origTicket)
        {
            try
            {
                string cmdTxt = string.Format("DELETE FROM " + _CLIENT_POSITIONS + " WHERE OrigTicket = {0}", origTicket);
                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveClientPosition method: " + e.Message);
            }
        }

        public void RemoveClientPosition_NotOpened(int managerCurTicket, int clientLogin)
        {
            try
            {
                string cmdTxt = string.Format("DELETE FROM " + _CLIENT_POSITIONS + " WHERE ManagerCurTicket = {0} AND Login = {1}", managerCurTicket, clientLogin);
                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveClientPosition method: " + e.Message);
            }
        }

        public void RemoveMultiClientPositions(List<int> origTickets)
        {
            if (origTickets.Count == 0)
            {
                return;
            }

            try
            {
                StringBuilder cmdTxt = new StringBuilder("DELETE FROM " + _CLIENT_POSITIONS + " WHERE OrigTicket IN ");
                cmdTxt.Append("(");
                foreach (int origTicket in origTickets)
                {
                    cmdTxt.Append(origTicket);
                    cmdTxt.Append(",");
                }
                cmdTxt.Remove(cmdTxt.Length - 1, 1);
                cmdTxt.Append(")");

                ExecuteNonQuery(cmdTxt.ToString());
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("RemoveMultiClientPositions method: " + e.Message);
            }
        }

        /// <summary>
        /// Gets client positions
        /// </summary>
        /// <returns>client positions by manager current ticket</returns>
        public Dictionary<int, List<PositionInfo>> GetClientPositions()
        {
            try
            {
                Dictionary<int, List<PositionInfo>> res = new Dictionary<int, List<PositionInfo>>();

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        StringBuilder cmdTxt = new StringBuilder();
                        cmdTxt.Append("SELECT all_pos.Login, all_pos.`Name`, all_pos.CurTicket, all_pos.OrigTicket, all_pos.Symbol, all_pos.Side, all_pos.CurVolume, all_pos.Multiplier, ");
                        cmdTxt.Append("all_pos.ManagerCurTicket, all_pos.OpenPrice, all_pos.Comment, all_pos.`Status`, all_pos.CloseVolume, all_pos.ClosePrice, all_pos.FullClose, all_pos.CloseHedge, all_pos.Attempts ");
                        cmdTxt.Append("FROM " + _CLIENT_POSITIONS + " all_pos ");
                        cmdTxt.Append("LEFT JOIN ");
                        cmdTxt.Append("(SELECT MAX(ID) AS ID FROM " + _CLIENT_POSITIONS + " GROUP BY Login, OrigTicket) AS cur_pos ");
                        cmdTxt.Append("ON all_pos.ID = cur_pos.ID ");
                        cmdTxt.Append("WHERE cur_pos.ID IS NOT NULL OR all_pos.OrigTicket = 0");

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt.ToString(), conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int login = Convert.ToInt32(reader[0]);
                                    string name = Convert.ToString(reader[1]);
                                    int curTicket = Convert.ToInt32(reader[2]);
                                    int origTicket = Convert.ToInt32(reader[3]);
                                    string symbol = Convert.ToString(reader[4]);
                                    int sideCode = Convert.ToInt32(reader[5]);
                                    int curVolume = Convert.ToInt32(reader[6]);
                                    double multiplier = Convert.ToDouble(reader[7]);
                                    int managerCurTicket = Convert.ToInt32(reader[8]);
                                    double openPrice = Convert.ToDouble(reader[9]);
                                    string comment = Convert.ToString(reader[10]);
                                    int status = Convert.ToInt32(reader[11]);
                                    int closeVolume = Convert.ToInt32(reader[12]);
                                    double closePrice = Convert.ToDouble(reader[13]);
                                    bool fullClose = Convert.ToBoolean(reader[14]);
                                    bool closeHedge = Convert.ToBoolean(reader[15]);
                                    int attempts = Convert.ToInt32(reader[16]);

                                    OrderSide side;
                                    switch (sideCode)
                                    {
                                        case 0:
                                            side = OrderSide.BUY;
                                            break;
                                        case 1:
                                            side = OrderSide.SELL;
                                            break;
                                        default:
                                            side = OrderSide.UNKNOWN;
                                            break;
                                    }

                                    if (!res.ContainsKey(managerCurTicket))
                                    {
                                        res[managerCurTicket] = new List<PositionInfo>();
                                    }
                                    res[managerCurTicket].Add(new PositionInfo(login, string.Empty, name, curTicket, origTicket, symbol, side, curVolume, multiplier, managerCurTicket, openPrice, comment, (PositionStatus)status, closeVolume, closePrice, fullClose, closeHedge, attempts));
                                }
                            }
                        }
                    }
                }

                return res;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetClientPositions method: " + e.Message);
            }
        }

        public HashSet<int> GetManagerTickets(DateTime startTime)
        {
            try
            {
                string cmdTxt = string.Format("SELECT Ticket FROM " + _MANAGER_TRADES + " WHERE Time >= '{0}'", startTime.Format());

                HashSet<int> managerTickets = new HashSet<int>();

                lock (_DBLocker)
                {
                    using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                    {
                        conn.Open();

                        using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                        {
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    managerTickets.Add(Convert.ToInt32(reader[0]));
                                }
                            }

                        }
                    }
                }

                return managerTickets;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetManagerTickets method: " + e.Message);
            }
        } 

        public DateTime GetManagerLastTradeTime()
        {
            try
            {
                string cmdTxt = "SELECT MAX(Time) FROM " + _MANAGER_TRADES;

                object queryRes = ExecuteScalar(cmdTxt);

                if (queryRes.Equals(System.DBNull.Value))
                {
                    return Constants.DEFAULT_TIME;
                }
                else
                {
                    return Convert.ToDateTime(queryRes);
                }               
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetManagerLastTradeTime method: " + e.Message);
            }
        }

        public DateTime GetClientLastTradeTime()
        {
            try
            {
                string cmdTxt = "SELECT MAX(Time) FROM " + _CLIENT_TRADES;

                object queryRes = ExecuteScalar(cmdTxt);

                if (queryRes.Equals(System.DBNull.Value))
                {
                    return Constants.DEFAULT_TIME;
                }
                else
                {
                    return Convert.ToDateTime(queryRes);
                }   
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetClientLastTradeTime method: " + e.Message);
            }
        }

        public bool IsPartialClose(int ticket)
        {
            try
            {
                string cmdTxt = string.Format("SELECT 1 FROM {0} WHERE Ticket = {1} AND Comment like 'partial close%' LIMIT 1", _MANAGER_TRADES, ticket);

                return (ExecuteScalar(cmdTxt) ?? DBNull.Value) != DBNull.Value;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("IsPartialClose method: " + e.Message);
            }
        }

        public void UpdateOpenClientPosition(int managerCurTicket, int clientLogin, int clientCurTicket, int status)
        {
            try
            {
                var cmdTxt = string.Format("UPDATE {0} SET CurTicket = {3}, OrigTicket = {3}, `Status` = {4} WHERE ManagerCurTicket = {1} AND Login = {2}",
                                            _CLIENT_POSITIONS, managerCurTicket, clientLogin, clientCurTicket, status);

                ExecuteNonQuery(cmdTxt.ToString());
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateClientPosition method: " + e.Message);
            }
        }

        public void UpdateClientPositionExecAttempts(int managerCurTicket, int clientLogin, int attempts)
        {
            try
            {
                var cmdTxt = string.Format("UPDATE {0} SET Attempts = {3} WHERE ManagerCurTicket = {1} AND Login = {2}",
                                            _CLIENT_POSITIONS, managerCurTicket, clientLogin, attempts);

                ExecuteNonQuery(cmdTxt.ToString());
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateClientPosition method: " + e.Message);
            }
        }

        public void UpdateClientTrade(DBClientTradeData trade)
        {
            try
            {
                int side;

                switch ((OrderSide)trade.OrderInfo.Side)
                {
                    case OrderSide.BUY:
                        side = 0;
                        break;
                    case OrderSide.SELL:
                        side = 1;
                        break;
                    default:
                        throw new Exception("Unknown order side.");
                }

                string cmdTxt;
                if (trade.Close)
                {
                    cmdTxt = string.Format("UPDATE {0} SET Ticket = {1}, Price = {2} WHERE Action = 1 AND Ticket = {3}",
                                                _CLIENT_TRADES, trade.OrderInfo.NewTicket, trade.OrderInfo.Price, trade.OrderInfo.Ticket);
                }
                else
                {
                    cmdTxt = string.Format("UPDATE {0} SET Ticket = {1}, OrigTicket = {1}, Price = {2} WHERE Action = 0 AND Ticket = 0 AND OrigTicket = 0 AND Login = {3} AND Symbol = '{4}' AND Side = {5} AND Volume = {6}",
                                                _CLIENT_TRADES, trade.OrderInfo.Ticket, trade.OrderInfo.Price, trade.OrderInfo.Login, trade.OrderInfo.Symbol, side, trade.OrderInfo.Volume);
                }

                ExecuteNonQuery(cmdTxt);
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateClientTrade method: " + e.Message);
            }
        }

        public void UpdateCloseClientPosition(int managerCurTicket, int clientLogin, int clientNewTicket, int status)
        {
            try
            {
                var cmdTxt = string.Format("UPDATE {0} SET CurTicket = {1}, `Status` = {2} WHERE ManagerCurTicket = {3} AND Login = {4}",
                                            _CLIENT_POSITIONS, clientNewTicket, status, managerCurTicket, clientLogin);

                ExecuteNonQuery(cmdTxt.ToString());
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("UpdateClientPosition method: " + e.Message);
            }
        }


        private int ExecuteNonQuery(string cmdTxt)
        {
            lock (_DBLocker)
            {
                using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                {
                    conn.Open();

                    using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                    {
                        return cmd.ExecuteNonQuery();
                    }
                }
            }         
        }

        private object ExecuteScalar(string cmdTxt)
        {
            lock (_DBLocker)
            {
                using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                {
                    conn.Open();

                    using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                    {
                        return cmd.ExecuteScalar();
                    }
                }
            }          
        }

        /*
        private MySqlDataReader ExecuteReader(string cmdTxt)
        {
            lock (_DBLocker)
            {
                using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                {
                    conn.Open();

                    using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                    {
                        return cmd.ExecuteReader();
                    }
                }
            }
        }
        */
    }

    [Serializable()]
    public class DBAccess_Exception : System.Exception
    {
        public DBAccess_Exception() : base() { }
        public DBAccess_Exception(string message) : base(message) { }
        public DBAccess_Exception(string message, System.Exception inner) : base(message, inner) { }
        protected DBAccess_Exception(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
}
