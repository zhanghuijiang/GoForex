using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
//using MT4;
using MT4ServerAPI;

namespace MAM
{    
    /// <summary>
    /// Holds all the logic for managing all the manager's and his clients' positions
    /// </summary>
    public class PositionAdmin
    {
        private ConcurrentDictionary<int, PositionInfo> _ManagerPositions; // by manager current ticket
        private ConcurrentDictionary<int, ConcurrentDictionary<int, PositionInfo>> _ClientPositions; // by manager current ticket, then by client login
        private ConcurrentDictionary<int, int> _ManagerTickets; // by client ticket to manager ticket
        private const int OpenTicketTimeout = 5; // minute timeout of open ticket in MT4


        public PositionAdmin()
        {
            _ManagerPositions = new ConcurrentDictionary<int, PositionInfo>();
            _ClientPositions = new ConcurrentDictionary<int, ConcurrentDictionary<int, PositionInfo>>();
            _ManagerTickets = new ConcurrentDictionary<int, int>();     
        }

        public bool AppendManagerPosition(PositionInfo managerPositionInfo)
        {
            if (_ManagerPositions.ContainsKey(managerPositionInfo.CurTicket)) { return false; }
            _ManagerPositions[managerPositionInfo.CurTicket] = managerPositionInfo;
            _ClientPositions[managerPositionInfo.CurTicket] = new ConcurrentDictionary<int, PositionInfo>(); // allocates a new dictionary for the client positions 
            return true;
        }

        public void AppendClientPosition(int managerCurTicket, PositionInfo clientPositionInfo)
        {
            _ClientPositions[managerCurTicket][clientPositionInfo.Login] = clientPositionInfo;
            if (clientPositionInfo.CurTicket > 0)
            {
                _ManagerTickets[clientPositionInfo.CurTicket] = managerCurTicket;
            }
            else
            {
                clientPositionInfo.CreateTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// When a manager position has been closed/partially closed we update its state
        /// </summary>
        /// <param name="managerOldTicket"></param>
        /// <param name="managerNewTicket"></param>
        /// <param name="closedVolume"></param>
        public void UpdateManagerPosition(int managerOldTicket, int managerNewTicket, int closedVolume)
        {
            PositionInfo managerPosition = _ManagerPositions[managerOldTicket];

            managerPosition.Update(managerNewTicket, closedVolume);
            managerPosition.Status = PositionStatus.CLOSE_NEW;
            
            if (managerNewTicket > 0)
            {
                // it's a partial close

                var clientPositions = _ClientPositions[managerOldTicket];

                // the client positions will be accessed via the new manager ticket
                ConcurrentDictionary<int, PositionInfo> removedClientPosition;
                if (!_ClientPositions.TryRemove(managerOldTicket, out removedClientPosition))
                {
                    throw new PositionAdmin_Exception(string.Format("UpdateManagerPosition method: Can't remove client position by manager ticket {0}", managerOldTicket));
                }
                _ClientPositions.AddOrUpdate(managerNewTicket, clientPositions, (k, v) => clientPositions);

                // update the manager current tickets
                foreach (PositionInfo positionInfo in clientPositions.Values)
                {
                    // update the dictionary (client ticket to manager ticket) to reference the new manager ticket
                    _ManagerTickets[positionInfo.CurTicket] = managerNewTicket;
                }

                // the manager position will be accessed via the new manager ticket
                PositionInfo removedManagerPosition;
                if (!_ManagerPositions.TryRemove(managerOldTicket, out removedManagerPosition))
                {
                    throw new PositionAdmin_Exception(string.Format("UpdateManagerPosition method: Can't remove client position by ticket {0}", managerOldTicket));
                }
                _ManagerPositions.AddOrUpdate(managerNewTicket, managerPosition, (k, v) => managerPosition);
            }
        }

        /// <summary>
        /// When a client position has been closed/partially closed we update its state
        /// </summary>
        /// <param name="clientOldTicket"></param>
        /// <param name="clientNewTicket"></param>
        /// <param name="closedVolume"></param>
        public void UpdateClientPosition(int clientOldTicket, int clientNewTicket, int closedVolume)
        {
            int managerCurTicket = GetManagerTicketByClient(clientOldTicket);
            PositionInfo clientPosition = _ClientPositions[managerCurTicket].Values.Where(it => it.CurTicket == clientOldTicket).FirstOrDefault();
            if (clientPosition == null)
            {
                throw new PositionAdmin_Exception(string.Format("UpdateClientPosition method: Can't find client position by ticket {0}", clientOldTicket));
            }
            clientPosition.Update(clientNewTicket, closedVolume);
            
            if (clientNewTicket > 0)
            {
                // it's a partial close

                // the manager ticket will be accessed via the new client ticket
                int removedTicket;
                if (!_ManagerTickets.TryRemove(clientOldTicket, out removedTicket))
                {
                    throw new PositionAdmin_Exception(string.Format("UpdateClientPosition method: Can't remove manager ticket {0}", clientOldTicket));
                }
                _ManagerTickets[clientNewTicket] = managerCurTicket;
            }
        }


        /// <summary>
        /// Applied when the manager has fully closed his position
        /// and all his clients' positions have been removed from the system
        /// </summary>
        /// <param name="managerCurTicket"></param>
        public void RemoveManagerPosition(int managerCurTicket)
        {
            if (ClientPositionsCountByManagerTicket(managerCurTicket) != 0)
            {
                throw new PositionAdmin_Exception(string.Format("RemoveManagerPosition method: Manager position with ticket {0} cannot be deleted because it has open client positions.", managerCurTicket));
            }
            else if (!_ManagerPositions[managerCurTicket].FullyClosed())
            {
                throw new PositionAdmin_Exception(string.Format("RemoveManagerPosition method: Manager position with ticket {0} cannot be deleted because it is still open.", managerCurTicket));
            }
            else
            {
                ConcurrentDictionary<int, PositionInfo> removedClientPosition;
                PositionInfo removedManagerPosition;
                if (!_ClientPositions.TryRemove(managerCurTicket, out removedClientPosition))
                {
                    throw new PositionAdmin_Exception(string.Format("RemoveManagerPosition method: Can't remove client position by manager ticket {0}.", managerCurTicket));
                }
                if (!_ManagerPositions.TryRemove(managerCurTicket, out removedManagerPosition))
                {
                    throw new PositionAdmin_Exception(string.Format("RemoveManagerPosition method: Can't remove manager position by ticket {0}.", managerCurTicket));
                }
            }
        }

        /// <summary>
        ///  Applied after a client position has been fully closed by the manager or closed/partially closed by the client himself
        /// </summary>
        /// <param name="clientCurTicket"></param>
        public void RemoveClientPosition(int clientLogin, int clientCurTicket)
        {
            int managerCurTicket = GetManagerTicketByClient(clientCurTicket);

            PositionInfo removedPosition;
            if (!_ClientPositions[managerCurTicket].TryRemove(clientLogin, out removedPosition))
            {
                throw new PositionAdmin_Exception(string.Format("RemoveClientPosition method: Can't remove client position by login {0}", clientLogin));
            }

            int removedTicket;
            if (!_ManagerTickets.TryRemove(clientCurTicket, out removedTicket))
            {
                throw new PositionAdmin_Exception(string.Format("RemoveClientPosition method: Can't remove manager ticket {0}", clientCurTicket));
            }
        }

        /// <summary>
        ///  Applied after a client position has been fully closed by the manager or closed/partially closed by the client himself
        /// </summary>
        /// <param name="clientCurTicket"></param>
        public void RemoveClientPosition_NotOpened(int managerCurTicket, int clientLogin)
        {
            PositionInfo removedPosition;
            if (!_ClientPositions[managerCurTicket].TryRemove(clientLogin, out removedPosition))
            {
                throw new PositionAdmin_Exception(string.Format("RemoveClientPosition method: Can't remove client position by login {0}", clientLogin));
            }
        }
        
        /*
        /// <summary>
        ///  Remove manager positions
        /// </summary>
        public void RemoveManager()
        {
            foreach (int managerCurTicket in _ManagerPositions.Keys)
            {
                ConcurrentDictionary<int, PositionInfo> removedClientPosition;
                if (!_ClientPositions.TryRemove(managerCurTicket, out removedClientPosition))
                {
                    throw new PositionAdmin_Exception(string.Format("RemoveManager method: Can't remove client position by manager ticket {0}.", managerCurTicket));
                }
                PositionInfo removedManagerPosition;
                if (!_ManagerPositions.TryRemove(managerCurTicket, out removedManagerPosition))
                {
                    throw new PositionAdmin_Exception(string.Format("RemoveManager method: Can't remove manager position by ticket {0}.", managerCurTicket));
                }
            }
        }
        */

        /*
        /// <summary>
        ///  Remove client positions and connection between client ticket and manager ticket
        /// </summary>
        /// <param name="clientLogin"></param>
        /// <returns>removed client origin ticket</returns>
        public List<int> RemoveClient(int clientLogin)
        {
            var clientOrigTickets = new List<int>();
            foreach (int managerCurTicket in _ClientPositions.Keys)
            {
                int clientCurTicket_toremove = 0;
                foreach (int clientCurTicket in _ClientPositions[managerCurTicket].Keys)
                {
                    if (_ClientPositions[managerCurTicket][clientCurTicket].Login == clientLogin)
                    {
                        clientOrigTickets.Add(_ClientPositions[managerCurTicket][clientCurTicket].OrigTicket);
                        clientCurTicket_toremove = clientCurTicket;
                        if (_ManagerTickets.ContainsKey(clientCurTicket))
                        {
                            int removedTicket;
                            if (!_ManagerTickets.TryRemove(clientCurTicket, out removedTicket))
                            {
                                throw new PositionAdmin_Exception(string.Format("RemoveClient method: Can't remove manager ticket {0}", clientCurTicket));
                            }
                        }
                    }
                }
                if (clientCurTicket_toremove > 0)
                {
                    PositionInfo removedPosition;
                    if (!_ClientPositions[managerCurTicket].TryRemove(clientCurTicket_toremove, out removedPosition))
                    {
                        throw new PositionAdmin_Exception(string.Format("RemoveClient method: Can't remove client position by ticket {0}", clientCurTicket_toremove));
                    }
                }
            }
            return clientOrigTickets;
        }
        */

        /*
        /// <summary>
        /// Gets all the current tickets owned by the manager
        /// </summary>
        /// <returns>manager current tickets</returns>
        public IEnumerable<int> GetManagerTickets()
        {
            return _ClientPositions.Keys;
        }
        */

        /*
        public ConcurrentDictionary<int, PositionInfo> GetManagerPositions()
        {
            return _ManagerPositions;
        }
        */

        /*
        public int ManagerPositionsCount()
        {
            return _ManagerPositions.Count;
        }
        */

        /*
        public int ClientPositionsCount()
        {
            int count = 0;

            foreach (int managerCurTicket in _ClientPositions.Keys)
            {
                count += ClientPositionsCountByManagerTicket(managerCurTicket);
            }

            return count;
        }
        */

        /// <summary>
        /// Gets the manager ticket by a specified client ticket
        /// </summary>
        /// <param name="clientCurTicket"></param>
        /// <returns>manager ticket</returns>
        public int GetManagerTicketByClient(int clientCurTicket)
        {
            return _ManagerTickets[clientCurTicket];
        }

        public PositionInfo GetManagerPosition(int managerCurTicket)
        {
            if (_ManagerPositions.ContainsKey(managerCurTicket))
            {
                return _ManagerPositions[managerCurTicket];
            }
            return null;
        }

        public PositionInfo GetClientPosition(int clientCurTicket)
        {
            return _ClientPositions[_ManagerTickets[clientCurTicket]].Values.Where(it => it.CurTicket == clientCurTicket).FirstOrDefault();
        }

        public ConcurrentDictionary<int, PositionInfo> GetClientPositions(int managerCurTicket)
        {
            if (_ClientPositions.ContainsKey(managerCurTicket))
            {
                return _ClientPositions[managerCurTicket];
            }
            return new ConcurrentDictionary<int, PositionInfo>();
        }

        public int ClientPositionsCountByManagerTicket(int managerCurTicket)
        {
            if (_ClientPositions.ContainsKey(managerCurTicket))
            {
                return _ClientPositions[managerCurTicket].Count;
            }
            return 0;
        }

        public int ClientPositionsCountByClientLogin(int clientLogin)
        {
            int count = 0;

            foreach (int managerCurTicket in _ClientPositions.Keys)
            {
                if (_ClientPositions[managerCurTicket].ContainsKey(clientLogin))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Indicates whether a specified ticket belongs to a manager
        /// </summary>
        /// <param name="managerCurTicket"></param>
        /// <returns>true/false</returns>
        public bool ManagerTicketExists(int managerCurTicket)
        {
            if (_ClientPositions.Keys.Contains(managerCurTicket))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Indicates whether a specified ticket belongs to a client
        /// </summary>
        /// <param name="clientCurTicket"></param>
        /// <returns>true/false</returns>
        public bool ClientTicketExists(int clientCurTicket)
        {
            if (_ManagerTickets.Keys.Contains(clientCurTicket))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Actually, semi-deep clone: managerPosInfo and clientPosInfo are not deep cloned
        /// </summary>
        /// <returns></returns>
        public PositionAdmin GetDeepClone()
        {
            PositionAdmin clonedPositionAdmin = new PositionAdmin();

            foreach (PositionInfo managerPosInfo in _ManagerPositions.Values)
            {
                clonedPositionAdmin.AppendManagerPosition(managerPosInfo);

                foreach (PositionInfo clientPosInfo in _ClientPositions[managerPosInfo.CurTicket].Values)
                {
                    clonedPositionAdmin.AppendClientPosition(managerPosInfo.CurTicket, clientPosInfo);
                }
            }

            return clonedPositionAdmin;
        }

        /// <summary>
        /// Find Accepted(In Queue) client trade
        /// </summary>
        /// <param name="OrderInfo">the new client ticket</param>
        /// <returns>manager current ticket</returns>
        public List<int> ManagerTicketOfAcceptedClientTrade(OrderInfo orderInfo)
        {
            var list = new List<int>();
            foreach (int managerCurTicket in _ClientPositions.Keys)
            {
                PositionInfo _position;
                if (_ClientPositions[managerCurTicket].TryGetValue(orderInfo.Login, out _position))
                {
                    if (_position.CurTicket == 0 && _position.Status == PositionStatus.OPEN_IN_PROCESS && _position.Symbol == orderInfo.Symbol &&
                        _position.Side == (OrderSide)orderInfo.Side && _position.CurVolume == orderInfo.Volume && managerCurTicket <= orderInfo.Ticket)
                    {
                        list.Add(managerCurTicket);
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Update Accepted(In Queue) client trade
        /// </summary>
        /// <param name="managerCurTicket">manager current ticket</param>
        /// <param name="OrderInfo">the new client ticket</param>
        public void UpdateAcceptedClientTrade(int managerCurTicket, int clientLogin, int clientTicket, double price)
        {
            PositionInfo clientPosition = _ClientPositions[managerCurTicket][clientLogin];
            clientPosition.UpdateAccepted(clientTicket, price);
            if (!_ManagerTickets.TryAdd(clientTicket, managerCurTicket))
            {
                throw new Exception(string.Format("Can't add ManagerTickets by manager ticket {0} and trade ticket {1}", managerCurTicket, clientTicket));
            }
        }

        /// <summary>
        /// Check Accepted(In Queue) client trade
        /// </summary>
        /// <returns>manager current ticket</returns>
        public List<PositionInfo> FindBadAcceptedClientTrade()
        {
            var problemPositions = new List<PositionInfo>();
            var checkTime = DateTime.UtcNow.AddMinutes(-OpenTicketTimeout);
            foreach (int managerCurTicket in _ClientPositions.Keys)
            {
                foreach (PositionInfo _pos in _ClientPositions[managerCurTicket].Values)
                {
                    if (_pos.CurTicket == 0 && _pos.Status == PositionStatus.OPEN_IN_PROCESS && _pos.CreateTime < checkTime)
                    {
                        problemPositions.Add(_pos);
                    }
                }
            }
            return problemPositions;
        }

        /// <summary>
        /// Find manager tickets that have client without open trades
        /// </summary>
        /// <returns>manager tickets</returns>
        public List<int> ManagerNotFinishedOpenTickets()
        {
            var list = new List<int>();
            foreach (var managerPosition in _ManagerPositions)
            {
                if (managerPosition.Value.Status == PositionStatus.OPEN_NEW || managerPosition.Value.Status == PositionStatus.OPEN_IN_PROCESS)
                {
                    //if (_ClientPositions[managerPosition.Key].Values.Any(it => it.Status == PositionStatus.OPEN_NEW))
                    //{
                        list.Add(managerPosition.Key);
                    //}
                }
            }
            return list;
        }

        /// <summary>
        /// Find manager tickets that have client without closed trades
        /// </summary>
        /// <returns>manager tickets</returns>
        public List<int> ManagerNotFinishedCloseTickets()
        {
            var list = new List<int>();
            foreach (var managerPosition in _ManagerPositions)
            {
                if (managerPosition.Value.Status == PositionStatus.CLOSE_NEW || managerPosition.Value.Status == PositionStatus.CLOSE_IN_PROCESS)
                {
                    //if (_ClientPositions[managerPosition.Key].Values.Any(it => it.Status == PositionStatus.CLOSE_NEW || it.Status == PositionStatus.CLOSE_IN_PROCESS))
                    //{
                        list.Add(managerPosition.Key);
                    //}
                }
            }
            return list;
        }
    }




    [Serializable()]
    public class PositionAdmin_Exception : System.Exception
    {
        public PositionAdmin_Exception() : base() { }
        public PositionAdmin_Exception(string message) : base(message) { }
        public PositionAdmin_Exception(string message, System.Exception inner) : base(message, inner) { }
        protected PositionAdmin_Exception(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
}
