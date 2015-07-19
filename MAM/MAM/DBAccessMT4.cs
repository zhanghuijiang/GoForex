using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;

namespace MAM
{
    public class DBAccessMT4
    {
        private readonly string _DBConnectionString;

        #region Private Classes

        private class Account_MT4
        {
            public int Login;
            public string Name;
            public decimal Balance;
            public decimal Equity;
            public double Rate;
        }

        #endregion

        public DBAccessMT4(string connectionString)
        {
            _DBConnectionString = connectionString;
        }

        public List<Account_MAM> GetAccounts(int managerLogin, string groups, int minBalance)
        {
            try
            {
                if (managerLogin <= 0) { return null; }
                if (string.IsNullOrEmpty(groups)) { return null; }
                if (minBalance <= 0) { return null; }


                var groupList = (groups ?? "").Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var sbGroups = new StringBuilder();
                foreach (var gr in groupList)
                {
                    sbGroups.Append((sbGroups.Length > 0 ? "," : "") + "'" + gr + "'");
                }
                if (sbGroups.Length == 0) { return null; }


                //string cmdTxt = "SELECT Login, `Group`, Currency, Balance, Equity, Symbol, " +
                string cmdTxt = "SELECT Login, `Name`, Balance, Equity, " +
                                "CASE WHEN COALESCE(BID,0) + COALESCE(ASK,0) = 0 THEN 1 ELSE CASE mt4_users.currency WHEN 'USD' THEN 1 WHEN 'JPY' THEN 2 / (BID + ASK) ELSE (BID + ASK) / 2 END END as Rate " +
                                "FROM mt4.mt4_users " +
                                "LEFT JOIN mt4.mt4_prices ON CASE mt4_users.currency WHEN 'EUR' THEN 'EURUSD' WHEN 'GBP' THEN 'GBPUSD' WHEN 'JPY' THEN 'USDJPY' END = mt4_prices.symbol " +
                                "WHERE (`Group` in (" + sbGroups.ToString() + ") AND `Balance` > " + minBalance + " AND Leverage > 20 " +
                                "AND Login NOT IN (select DISTINCT AGENT_ACCOUNT FROM mt4.mt4_users)) " +
                                "OR Login = " + managerLogin;

                var accList = new List<Account_MT4>();
                Account_MT4 manager = null;

                using (MySqlConnection conn = new MySqlConnection(_DBConnectionString))
                {
                    conn.Open();
                    using (MySqlCommand cmd = new MySqlCommand(cmdTxt, conn))
                    {
                        using (MySqlDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var item = new Account_MT4()
                                {
                                    Login = Convert.ToInt32(reader[0]),
                                    Name = Convert.ToString(reader[1]),
                                    Balance = Convert.ToDecimal(reader[2]),
                                    Equity = Convert.ToDecimal(reader[3]),
                                    Rate = Convert.ToDouble(reader[4])
                                };
                                if (item.Login == managerLogin)
                                {
                                    manager = item;
                                }
                                else
                                {
                                    accList.Add(item);
                                }
                            }
                        }
                    }
                }

                if (manager == null) { return null; }
                if (accList.Count == 0) { return null; }

                var result = new List<Account_MAM>();

                accList.ForEach(it => {
                    var m = ((double)it.Equity * it.Rate) / ((double)manager.Equity * manager.Rate);
                    m = Math.Truncate(m * 10) / 10;
                    result.Add(new Account_MAM() { Login = it.Login, Name = it.Name, Multiplier = (m < 1 ? -1 * m : m) });
                });

                return result;
            }
            catch (Exception e)
            {
                throw new DBAccess_Exception("GetAccounts method: " + e.Message);
            }
        }

    }

    public class Account_MAM
    {
        public int Login;
        public string Name;
        public double Multiplier;
    }
}
