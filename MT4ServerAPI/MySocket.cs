using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MT4ServerAPI
{
    public enum eMsgType
    {
        INFO,
        ERR
    }

    class MySocket
    {
        public delegate void dgtOnLogin(SReturnCode Obj);
        public delegate void dgtOnOpenOrder(SOrderReturnCode Obj);
        public delegate void dgtOnCloseOrder(SOrderReturnCode Obj);
        public delegate void dgtOnGetTrades(SGetTradesReturn Obj);
        public delegate void dgtOnTradeAdd(STradesAdd Obj);
        public delegate void dgtOnTradesAdd(List<STradesAdd> Obj);
        public delegate void dgtOnMsg(string Obj, eMsgType Type);

        public event dgtOnLogin OnLogin;
        public event dgtOnOpenOrder OnOpenOrder;
        public event dgtOnCloseOrder OnCloseOrder;
        public event dgtOnGetTrades OnGetTrades;
        public event dgtOnTradeAdd OnTradeAdd;
        public event dgtOnTradesAdd OnTradesAdd;
        public event dgtOnMsg OnMsg;

        public MySocket()
        {
            //ArrEvents = new ManualResetEvent[(int)eEvents.e_total];
            //for (int ii = 0; ii < ArrEvents.Length; ++ii)
            //{
            //    ArrEvents[ii] = new ManualResetEvent(true);
            //}
        }

        public static void SetTcpKeepAlive(Socket socket, uint keepaliveTime, uint keepaliveInterval)
        {
            /* the native structure SIO_KEEPALIVE_VALS
            The value specified in the onoff member determines if TCP keep-alive is enabled or disabled. If the onoff member is set to a nonzero value, TCP keep-alive is enabled and the other members in the structure are used. 
            The keepalivetime member specifies the timeout, in milliseconds, with no activity until the first keep-alive packet is sent.
            The keepaliveinterval member specifies the interval, in milliseconds, between when successive keep-alive packets are sent if no acknowledgement is received. 
            struct tcp_keepalive {
            ULONG onoff;
            ULONG keepalivetime;
            ULONG keepaliveinterval;
            };
            */

            // marshal the equivalent of the native structure into a byte array
            uint dummy = 0;
            byte[] inOptionValues = new byte[Marshal.SizeOf(dummy) * 3];
            BitConverter.GetBytes((uint)(keepaliveTime)).CopyTo(inOptionValues, 0);
            BitConverter.GetBytes((uint)keepaliveTime).CopyTo(inOptionValues, Marshal.SizeOf(dummy));
            BitConverter.GetBytes((uint)keepaliveInterval).CopyTo(inOptionValues, Marshal.SizeOf(dummy) * 2);

            // write SIO_VALS to Socket IOControl
            socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
        }
        
        public bool Connect(string IP, int Port)
        {
            bool ret = true;
            try
            {
                if (Socket.Connected)
                    return true;
                SetTcpKeepAlive(Socket.Client, 10000, 5000); // 10 sec at the first keep alive packet and for consecutive keep alive packets 5 sec.
                Socket.Connect(IP, Port);
                ThreadRead = new Thread(new ThreadStart(MainLoop));
                ThreadRead.Start();
            }
            catch(ObjectDisposedException ex)
            {
                Socket = new TcpClient();
                Connect(IP, Port);
            }
            catch (Exception ex)
            {
                ret = false;
                OnMsg(ex.Message, eMsgType.ERR);
            }
            return ret;
        }

        public bool Disconnect()
        {
            bool ret = true;
            LoggedIn = false;
            Socket.Close();
            return ret;
        }

        Response AddMessageToMap(SBase Obj)
        {
            Response ret = new Response(Obj);
            int count = 0;
            bool add = MapMessages.TryAdd(Obj.ClientTime, ret);
            while (!add && ++count < 10)
            {
                Thread.Sleep(1);
                Obj.ClientTime = DateTime.Now.Ticks;
                add = MapMessages.TryAdd(Obj.ClientTime, ret);
            }
            if (! add)
                throw new Exception(string.Format("AddMessageToMap Failure for message {0}", Obj.MessageType));
            return ret;
        }

        bool RemoveMessageFromMap(SBase Obj)
        {
            Response res = new Response(Obj);
            return MapMessages.TryRemove(Obj.ClientTime, out res);
        }

        public bool SendLogin(int Login, string Password)
        {
            if (!Socket.Connected)
                return false;
            //if (! ArrEvents[(int)eEvents.e_return_msg].WaitOne(5000))
            //{
            //    OnMsg(string.Format("SendLogin login = {0} : another call is in progress", Login), eMsgType.ERR);
            //    return false;
            //}
            //ArrEvents[(int)eEvents.e_return_msg].Reset();
            
            // Login
            int size = Marshal.SizeOf(typeof(SLogin));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            SLogin obj = new SLogin();
            obj.User = Login.ToString();
            obj.Password = Password;

            RawSerializer<SLogin> rs = new RawSerializer<SLogin>();
            Response item = AddMessageToMap(obj);
            if (!Send<SLogin>(rs, obj))
            {
                RemoveMessageFromMap(obj);
                OnMsg(string.Format("SendLogin login = {0} : socket failed to send this message.", Login), eMsgType.ERR);
                return false;
            }

            //if (ArrEvents[(int)eEvents.e_return_msg].WaitOne(5000))
            if (item._event.WaitOne(5000))
            {
                if (! MapMessages.TryGetValue(obj.ClientTime, out item))
                {
                    OnMsg(string.Format("SendLogin: fail to retrieve message from map {0}", obj.ClientTime), eMsgType.ERR);
                    return false;
                }
                RemoveMessageFromMap(obj);
                LoggedIn = ((SReturnCode)item._response).ReturnCode == eReturnCode.E_Success;
                return LoggedIn;
            }
            else
            {
                OnMsg(string.Format("SendLogin ({0}) Timeout", Login), eMsgType.ERR);
            }
            return false;
        }

        public int SendOpenOrder(int login, eOrderSide side, string symbol, int volume, double Price, string comment, ref string errorMsg, ref DateTime openTime)
        {
            if (!Socket.Connected)
            {
                errorMsg = "No connection";
                return -1;
            }
            else if (!LoggedIn)
            {
                errorMsg = "Not Logged in";
                return -1;
            }
            //if (!ArrEvents[(int)eEvents.e_order_return_msg].WaitOne(5000))
            //{
            //    OnMsg(string.Format("SendOpenOrder login = {0} : another call is in progress", login), eMsgType.ERR);
            //    return -1;
            //}
            //ArrEvents[(int)eEvents.e_order_return_msg].Reset();
            // OpenOrder
            int size = Marshal.SizeOf(typeof(SOpenOrder));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            SOpenOrder obj = new SOpenOrder();
            obj.OrderInfo.Login = login;
            obj.OrderInfo.OrderSide = side;
            obj.OrderInfo.Symbol = symbol;
            obj.OrderInfo.Price = Price;
            obj.OrderInfo.Volume = volume;
            obj.OrderInfo.Comment = comment;

            RawSerializer<SOpenOrder> rs = new RawSerializer<SOpenOrder>();
            Response item = AddMessageToMap(obj);
            if (!Send<SOpenOrder>(rs, obj))
            {
                RemoveMessageFromMap(obj);
                OnMsg(string.Format("SendOpenOrder login = {0} : socket failed to send this message.", login), eMsgType.ERR);
                return -1;
            }
            //if (ArrEvents[(int)eEvents.e_order_return_msg].WaitOne(10000))
            if (item._event.WaitOne(10000))
            {
                if (MapMessages.TryGetValue(obj.ClientTime, out item))
                {
                    RemoveMessageFromMap(obj);
                    SOrderReturnCode response = (SOrderReturnCode)item._response;
                    if (response.ReturnCode != eReturnCode.E_Success)
                    {
                        errorMsg = response.ErrorDescription;
                        return -1;
                    }
                    else
                    {
                        openTime = Conversions.time_t2DateTime(response.ServerTime);
                    }
                    return response.OrderID;
                }
                else
                {
                    OnMsg(string.Format("SendOpenOrder: fail to retrieve message from map {0}", obj.ClientTime), eMsgType.ERR);
                }
            }
            else
            {
                errorMsg = string.Format("SendOpenOrder ({0}) Timeout", login);
            }
            return -1;
        }

        public int SendCloseOrder(int Ticket, eOrderSide side, string symbol, int volume, double Price, string comment, bool bPartial, ref string errorMsg, ref DateTime closeTime)
        {
            if (!Socket.Connected)
            {
                errorMsg = "No connection";
                return -1;
            }
            else if (!LoggedIn)
            {
                errorMsg = "Not Logged in";
                return -1;
            }
            //if (!ArrEvents[(int)eEvents.e_order_return_msg].WaitOne(5000))
            //{
            //    OnMsg(string.Format("SendCloseOrder Ticket = {0} : another call is in progress", Ticket), eMsgType.ERR);
            //    return -1;
            //}
            //ArrEvents[(int)eEvents.e_order_return_msg].Reset();
            // CloseOrder
            int size = Marshal.SizeOf(typeof(SCloseOrder));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            SCloseOrder obj = new SCloseOrder();
            obj.OrderInfo.Ticket = Ticket;
            obj.OrderInfo.OrderSide = side;
            obj.OrderInfo.Symbol = symbol;
            obj.OrderInfo.Price = Price;
            obj.OrderInfo.Volume = volume;
            obj.OrderInfo.Comment = comment;
            
            RawSerializer<SCloseOrder> rs = new RawSerializer<SCloseOrder>();
            Response item = AddMessageToMap(obj);
            if (!Send<SCloseOrder>(rs, obj))
            {
                RemoveMessageFromMap(obj);
                OnMsg(string.Format("SendCloseOrder Ticket = {0} : socket failed to send this message.", Ticket), eMsgType.ERR);
                return -1;
            }
            //if (ArrEvents[(int)eEvents.e_order_return_msg].WaitOne(10000))
            if (item._event.WaitOne(10000))
            {
                if (MapMessages.TryGetValue(obj.ClientTime, out item))
                {
                    RemoveMessageFromMap(obj);
                    SOrderReturnCode response = (SOrderReturnCode)item._response;
                    if (response.ReturnCode != eReturnCode.E_Success)
                    {
                        errorMsg = response.ErrorDescription;
                        return -1;
                    }
                    else
                    {
                        closeTime = Conversions.time_t2DateTime(response.ServerTime);
                    }
                    MT4ServerManager.log.InfoFormat("{0} IsPartial = {1}", response.OrderID, response.IsPartial);
                    return response.IsPartial != 0 ? response.OrderID : 0;
                }
                else
                {
                    OnMsg(string.Format("SendCloseOrder: fail to retrieve message from map {0}", obj.ClientTime), eMsgType.ERR);
                }
            }
            else
            {
                errorMsg = string.Format("SendCloseOrder({0}) Timeout", Ticket);
            }
            return -1;
        }

        public List<STradesAdd> SendGetTrades(int Login, string Group, DateTime From, DateTime To, eGetOrderType getOrderType)
        {
            if (!Socket.Connected)
            {
                OnMsg("No connection", eMsgType.ERR);
                return null;
            }
            else if (!LoggedIn)
            {
                OnMsg("Not Logged in", eMsgType.ERR);
                return null;
            }
            //if (!ArrEvents[(int)eEvents.e_get_trades].WaitOne(5000))
            //{
            //    OnMsg(string.Format("SendGetTrades login = {0} : another call is in progress", Login), eMsgType.ERR);
            //    return null;
            //}
            //ArrEvents[(int)eEvents.e_get_trades].Reset();
            // GetTrades
            int size = Marshal.SizeOf(typeof(SGetTrades));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            SGetTrades obj = new SGetTrades();
            obj.login = Login;
            obj.Group = Group;
            obj.SetFrom(From);
            obj.SetTo(To);
            obj.GetOrderType = getOrderType;
            
            RawSerializer<SGetTrades> rs = new RawSerializer<SGetTrades>();
            Response item = AddMessageToMap(obj);
            if (!Send<SGetTrades>(rs, obj))
            {
                RemoveMessageFromMap(obj);
                OnMsg(string.Format("SendGetTrades login = {0} : socket failed to send this message.", Login), eMsgType.ERR);
                return null;
            }
            //if (ArrEvents[(int)eEvents.e_get_trades].WaitOne(60000))
            if (item._event.WaitOne(60000))
            {
                if (MapMessages.TryGetValue(obj.ClientTime, out item))
                {
                    RemoveMessageFromMap(obj);
                    SGetTrades response = (SGetTrades)item._response;
                    return item._trades;
                }
                else
                {
                    OnMsg(string.Format("SendGetTrades: fail to retrieve message from map {0}", obj.ClientTime), eMsgType.ERR);
                }
            }
            else
            {
                OnMsg(string.Format("Timeout calling GetTrades({0}, {1}, {2}, {3})", Login, From, To, getOrderType), eMsgType.ERR);
            }
            return null;
        }

        public bool SendCreateUser(int login, string name, string psw, string group, int deposit)
        {
            if (!Socket.Connected)
            {
                OnMsg("No connection", eMsgType.ERR);
                return false;
            }
            else if (!LoggedIn)
            {
                OnMsg("Not Logged in", eMsgType.ERR);
                return false;
            }
            //if (!ArrEvents[(int)eEvents.e_return_msg].WaitOne(5000))
            //{
            //    OnMsg(string.Format("SendCreateUser login = {0} : another call is in progress", login), eMsgType.ERR);
            //    return false;
            //}
            //ArrEvents[(int)eEvents.e_return_msg].Reset();
            // CreateUser
            int size = Marshal.SizeOf(typeof(SCreateUser));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            SCreateUser obj = new SCreateUser();
            obj.Login = login;
            obj.Name = name;
            obj.Password = psw;
            obj.Group = group;
            obj.Deposit = deposit;

            RawSerializer<SCreateUser> rs = new RawSerializer<SCreateUser>();
            Response item = AddMessageToMap(obj);
            if (!Send<SCreateUser>(rs, obj))
            {
                RemoveMessageFromMap(obj);
                OnMsg(string.Format("SendCreateUser login = {0} : socket failed to send this message.", login), eMsgType.ERR);
                return false;
            }

            //if (ArrEvents[(int)eEvents.e_return_msg].WaitOne(5000))
            if (item._event.WaitOne(5000))
            {
                if (MapMessages.TryGetValue(obj.ClientTime, out item))
                {
                    RemoveMessageFromMap(obj);
                    SReturnCode return_msg = (SReturnCode)item._response;
                    return return_msg.ReturnCode == eReturnCode.E_Success;
                }
                else
                {
                    OnMsg(string.Format("SendCreateUser: fail to retrieve message from map {0}", obj.ClientTime), eMsgType.ERR);
                    return false;
                }
            }
            else
            {
                OnMsg(string.Format("SendCreateUser ({0}) Timeout", login), eMsgType.ERR);
            }
            return false;
        }

        public SGetUserInfo SendGetUserInfo(int login)
        {
            SGetUserInfo ret = null;
            if (!Socket.Connected)
            {
                OnMsg("No connection", eMsgType.ERR);
                return ret;
            }
            else if (!LoggedIn)
            {
                OnMsg("Not Logged in", eMsgType.ERR);
                return ret;
            }
            //if (!ArrEvents[(int)eEvents.e_get_user_info].WaitOne(5000))
            //{
            //    OnMsg(string.Format("SendGetUserInfo login = {0} : another call is in progress", login), eMsgType.ERR);
            //    return ret;
            //}
            //ArrEvents[(int)eEvents.e_get_user_info].Reset();
            // GetUserInfo
            int size = Marshal.SizeOf(typeof(SGetUserInfoReq));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            SGetUserInfoReq obj = new SGetUserInfoReq();
            obj.Login = login;

            RawSerializer<SGetUserInfoReq> rs = new RawSerializer<SGetUserInfoReq>();
            Response item = AddMessageToMap(obj);
            if (!Send<SGetUserInfoReq>(rs, obj))
            {
                RemoveMessageFromMap(obj);
                OnMsg(string.Format("SendGetUserInfo login = {0} : socket failed to send this message.", login), eMsgType.ERR);
                return ret;
            }

            //if (ArrEvents[(int)eEvents.e_get_user_info].WaitOne(5000))
            if (item._event.WaitOne(5000))
            {
                if (MapMessages.TryGetValue(obj.ClientTime, out item))
                {
                    RemoveMessageFromMap(obj);
                    ret = (SGetUserInfo)item._response;
                    if (ret.ReturnCode != eReturnCode.E_Success)
                    {
                        OnMsg(ret.ErrorDescription, eMsgType.ERR);
                    }
                }
                else
                {
                    OnMsg(string.Format("SendGetUserInfo: fail to retrieve message from map {0}", obj.ClientTime), eMsgType.ERR);
                }
                return ret;
            }
            else
            {
                OnMsg(string.Format("Timeout send get user info request for login {0}", login), eMsgType.ERR);
            }
            return ret;
        }

        public DateTime SendGetServerTime()
        {
            if (!Socket.Connected)
                return DateTime.Now.AddMonths(-1);
            //if (!ArrEvents[(int)eEvents.e_get_server_time].WaitOne(5000))
            //{
            //    OnMsg(string.Format("SendGetServerTime : another call is in progress"), eMsgType.ERR);
            //    return DateTime.MinValue; ;
            //}
            //ArrEvents[(int)eEvents.e_get_server_time].Reset();
            // Login
            int size = Marshal.SizeOf(typeof(SLogin));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            SGetServerTime obj = new SGetServerTime();
            
            RawSerializer<SGetServerTime> rs = new RawSerializer<SGetServerTime>();
            Response item = AddMessageToMap(obj);
            if (!Send<SGetServerTime>(rs, obj))
            {
                RemoveMessageFromMap(obj);
                OnMsg(string.Format("SendGetServerTime : socket failed to send this message."), eMsgType.ERR);
                return DateTime.Now.AddMonths(-1);
            }

            //if (ArrEvents[(int)eEvents.e_get_server_time].WaitOne(5000))
            if (item._event.WaitOne(5000))
            {
                if (MapMessages.TryGetValue(obj.ClientTime, out item))
                {
                    RemoveMessageFromMap(obj);
                    SGetServerTime get_server_time = (SGetServerTime)item._response;
                    return Conversions.time_t2DateTime(get_server_time.ServerTime);
                }
                else
                {
                    OnMsg(string.Format("SendGetServerTime: fail to retrieve message from map {0}", obj.ClientTime), eMsgType.ERR);
                }

            }
            else
            {
                OnMsg(string.Format("SendGetServerTime Timeout"), eMsgType.ERR);
            }
            return DateTime.Now.AddMonths(-1);
        }

        object m_readsync = new object();
        object m_writesync = new object();
        bool Send<T>(RawSerializer<T> rawSerializer, T Obj)
        {
            bool ret = true;
            try
            {
                NetworkStream stream = Socket.GetStream();
                byte[] arr = rawSerializer.RawSerialize(Obj);
                lock (m_writesync)
                {
                    stream.Write(arr, 0, arr.Length);
                }
            }
            catch (Exception e)
            {
                ret = false;
                OnMsg(string.Format("Send:Exception: {0}", e), eMsgType.ERR);
            }
            return ret;
        }

        bool ReadString(byte[] Buffer, int Offset, int MaxLen, NetworkStream stream)
        {
            bool ret = false;
            try
            {
                if (!Socket.Connected || Buffer == null || MaxLen <= 0)
                    return false;
                int res = 0;
                int offset = Offset;
                while (MaxLen > 0)
                {
                    while (! stream.DataAvailable)
                    {
                        Thread.Sleep(50);
                    }
                    lock (m_readsync)
                    {
                        res = stream.Read(Buffer, offset, Math.Min(MaxLen, BUFFER_SIZE));
                    }
                    MaxLen -= res;
                    offset += res;
                }
                ret = (MaxLen <= 0);
            }
            catch (Exception ex)
            {
                MT4ServerManager.log.ErrorFormat("ReadString exception: {0}", ex.Message);
                OnMsg(ex.Message, eMsgType.ERR);
                ret = false;
            }
            return ret;
        }
        void MainLoop()
        {
            try
            {
                int message_type = (int)eMessageType.E_InvalidMessageType;

                // Get a client stream for reading and writing. 
                NetworkStream stream = Socket.GetStream();

                // Buffer to store the response bytes.
                byte[] data = new Byte[BUFFER_SIZE];

                // Read the first batch of the TcpServer response bytes.
                while (Socket.Connected)
                {
                    // Message Type
                    if (ReadString(data, 0, Marshal.SizeOf(typeof(int)), stream))
                    {
                        message_type = BitConverter.ToInt32(data, 0);
                        System.Diagnostics.Debug.WriteLine(string.Format("{0} {1}", DateTime.Now, message_type));
                        //if (message_type == (int)eMessageType.E_OrderReturnCode)
                        //{
                        //    int yyy = 0;
                        //}
                    }
                    
                    if (message_type == (int)eMessageType.E_ReturnCode)
                    {
                        if (ReadString(data, Marshal.SizeOf(typeof(int)), Marshal.SizeOf(typeof(SReturnCode)) - Marshal.SizeOf(typeof(int)), stream))
                        {
                            IntPtr unmanagedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SReturnCode)));
                            Marshal.Copy(data, 0, unmanagedPointer, Marshal.SizeOf(typeof(SReturnCode)));
                            // Call unmanaged code
                            //ReturnCode = (SReturnCode)Marshal.PtrToStructure(unmanagedPointer, typeof(SReturnCode));
                            SReturnCode return_msg = (SReturnCode)Marshal.PtrToStructure(unmanagedPointer, typeof(SReturnCode));
                            Marshal.FreeHGlobal(unmanagedPointer);
                            //EventRequestDone.Set();
                            //ArrEvents[(int)eEvents.e_return_msg].Set();
                            Response response;
                            if (MapMessages.TryGetValue(return_msg.ClientTime, out response))
                            {
                                Response new_response = new Response(return_msg, response._event);
                                MapMessages.TryUpdate(return_msg.ClientTime, new_response, response);
                                response._event.Set();
                                //RemoveMessageFromMap(ReturnCode);
                            }
                        }
                    }
                    else if (message_type == (int)eMessageType.E_OrderReturnCode)
                    {
                        if (ReadString(data, Marshal.SizeOf(typeof(int)), Marshal.SizeOf(typeof(SOrderReturnCode)) - Marshal.SizeOf(typeof(int)), stream))
                        {
                            IntPtr unmanagedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SOrderReturnCode)));
                            Marshal.Copy(data, 0, unmanagedPointer, Marshal.SizeOf(typeof(SOrderReturnCode)));
                            // Call unmanaged code
                            SOrderReturnCode return_msg = (SOrderReturnCode)Marshal.PtrToStructure(unmanagedPointer, typeof(SOrderReturnCode));
                            Marshal.FreeHGlobal(unmanagedPointer);
                            //EventRequestDone.Set();
                            
                            Response response;
                            if (MapMessages.TryGetValue(return_msg.ClientTime, out response))
                            {
                                Response new_response = new Response(return_msg, response._event);
                                MapMessages.TryUpdate(return_msg.ClientTime, new_response, response);
                                response._event.Set();
                                //RemoveMessageFromMap(ReturnCode);
                            }

                            //ArrEvents[(int)eEvents.e_order_return_msg].Set();
                            if (return_msg.CalledMessageType == eMessageType.E_OpenOrder)
                            {
                                OnOpenOrder(return_msg);
                            }
                            else if (return_msg.CalledMessageType == eMessageType.E_CloseOrder)
                            {
                                OnCloseOrder(return_msg);
                            }
                        }
                    }
                    else if (message_type == (int)eMessageType.E_GetTradesReturn)
                    {
                        if (ReadString(data, Marshal.SizeOf(typeof(int)), Marshal.SizeOf(typeof(SGetTradesReturn)) - Marshal.SizeOf(typeof(int)), stream))
                        {
                            IntPtr unmanagedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SGetTradesReturn)));
                            Marshal.Copy(data, 0, unmanagedPointer, Marshal.SizeOf(typeof(SGetTradesReturn)));
                            // Call unmanaged code
                            SGetTradesReturn return_msg = (SGetTradesReturn)Marshal.PtrToStructure(unmanagedPointer, typeof(SGetTradesReturn));
                            Marshal.FreeHGlobal(unmanagedPointer);
                            //EventRequestDone.Set();
                            Response response;
                            if (return_msg.Total <= 0)
                            {
                                //EventRequestDone.Set(); // Set event because E_GetTrades shall not be sent for empty data
                                //ArrEvents[(int)eEvents.e_get_trades].Set();
                                if (MapMessages.TryGetValue(return_msg.ClientTime, out response))
                                {
                                    response._event.Set();
                                    //RemoveMessageFromMap(return_msg);
                                }
                            }
                            else
                            {
                                if (MapMessages.TryGetValue(return_msg.ClientTime, out response))
                                {
                                    Response new_response = new Response(return_msg, response._event);
                                    MapMessages.TryUpdate(return_msg.ClientTime, new_response, response);
                                    new_response._trades = new List<STradesAdd>();
                                    //RemoveMessageFromMap(return_msg);
                                }
                            }
                            OnGetTrades(return_msg);
                        }
                    }
                    else if (message_type == (int)eMessageType.E_GetTrades)
                    {
                        if (ReadString(data, Marshal.SizeOf(typeof(int)), Marshal.SizeOf(typeof(STradesAdd)) - Marshal.SizeOf(typeof(int)), stream))
                        {
                            IntPtr unmanagedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(STradesAdd)));
                            Marshal.Copy(data, 0, unmanagedPointer, Marshal.SizeOf(typeof(STradesAdd)));
                            // Call unmanaged code
                            STradesAdd return_msg = (STradesAdd)Marshal.PtrToStructure(unmanagedPointer, typeof(STradesAdd));
                            Marshal.FreeHGlobal(unmanagedPointer);
                            Response response;
                            if (MapMessages.TryGetValue(return_msg.ClientTime, out response))
                            {
                                response._trades.Add(return_msg);
                                if (response._trades.Count == ((SGetTradesReturn)response._response).Total)
                                {
                                    response._event.Set();
                                    OnTradesAdd(response._trades);
                                }
                                //RemoveMessageFromMap(return_msg);
                            }
                            //ListTradesAdd.Add(obj_ret);
                            //if (ListTradesAdd.Count >= GetTradesReturn.Total)
                            //{
                            //    GetTradesReturn.Total = 0;
                            //    //EventRequestDone.Set();
                            //    ArrEvents[(int)eEvents.e_get_trades].Set();
                            //    OnTradesAdd(ListTradesAdd.ToArray());
                            //}
                        }
                    }
                    else if (message_type == (int)eMessageType.E_TradesAdd)
                    {
                        if (ReadString(data, Marshal.SizeOf(typeof(int)), Marshal.SizeOf(typeof(STradesAdd)) - Marshal.SizeOf(typeof(int)), stream))
                        {
                            IntPtr unmanagedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(STradesAdd)));
                            Marshal.Copy(data, 0, unmanagedPointer, Marshal.SizeOf(typeof(STradesAdd)));
                            // Call unmanaged code
                            STradesAdd obj_ret = (STradesAdd)Marshal.PtrToStructure(unmanagedPointer, typeof(STradesAdd));
                            Marshal.FreeHGlobal(unmanagedPointer);
                            MT4ServerManager.log.Info("Before Call to OnTradeAdd");
                            OnTradeAdd(obj_ret);
                            MT4ServerManager.log.Info("After Call to OnTradeAdd");
                        }
                    }
                    else if (message_type == (int)eMessageType.E_GetUserInfo)
                    {
                        if (ReadString(data, Marshal.SizeOf(typeof(int)), Marshal.SizeOf(typeof(SGetUserInfo)) - Marshal.SizeOf(typeof(int)), stream))
                        {
                            IntPtr unmanagedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SGetUserInfo)));
                            Marshal.Copy(data, 0, unmanagedPointer, Marshal.SizeOf(typeof(SGetUserInfo)));
                            // Call unmanaged code
                            SGetUserInfo return_msg = (SGetUserInfo)Marshal.PtrToStructure(unmanagedPointer, typeof(SGetUserInfo));
                            Marshal.FreeHGlobal(unmanagedPointer);
                            //EventRequestDone.Set();
                            Response response;
                            if (MapMessages.TryGetValue(return_msg.ClientTime, out response))
                            {
                                Response new_response = new Response(return_msg, response._event);
                                MapMessages.TryUpdate(return_msg.ClientTime, new_response, response);
                                response._event.Set();
                                //RemoveMessageFromMap(ReturnCode);
                            }
                            //ArrEvents[(int)eEvents.e_get_user_info].Set();
                        }
                    }
                    else if (message_type == (int)eMessageType.E_GetServerTime)
                    {
                        if (ReadString(data, Marshal.SizeOf(typeof(int)), Marshal.SizeOf(typeof(SGetServerTime)) - Marshal.SizeOf(typeof(int)), stream))
                        {
                            IntPtr unmanagedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SGetServerTime)));
                            Marshal.Copy(data, 0, unmanagedPointer, Marshal.SizeOf(typeof(SGetServerTime)));
                            // Call unmanaged code
                            SGetServerTime return_msg = (SGetServerTime)Marshal.PtrToStructure(unmanagedPointer, typeof(SGetServerTime));
                            Marshal.FreeHGlobal(unmanagedPointer);
                            //EventRequestDone.Set();
                            //ArrEvents[(int)eEvents.e_get_server_time].Set();
                            Response response;
                            if (MapMessages.TryGetValue(return_msg.ClientTime, out response))
                            {
                                Response new_response = new Response(return_msg, response._event);
                                MapMessages.TryUpdate(return_msg.ClientTime, new_response, response);
                                response._event.Set();
                                //RemoveMessageFromMap(ReturnCode);
                            }
                        }
                    }
                }
                // Close everything.
                stream.Close();
                Socket.Close();
            }
            catch (ArgumentNullException e)
            {
                OnMsg(string.Format("MainLoop:ArgumentNullException: {0}", e), eMsgType.ERR);
            }
            catch (SocketException e)
            {
                OnMsg(string.Format("MainLoop:SocketException: {0}", e), eMsgType.ERR);
            }
            catch (Exception e)
            {
                OnMsg(string.Format("MainLoop:Exception: {0}", e), eMsgType.ERR);
            }
        }

        //void OnLogin(SReturnCode returnCode)
        //{

        //}

        //dgtOnLogin OnLogin;
        private const int BUFFER_SIZE = 4096;
        TcpClient Socket = new TcpClient();
        Thread ThreadRead = null;
        public bool LoggedIn { get { return _LoggedIn; } private set { _LoggedIn = value; } }
        bool _LoggedIn = false;

        //enum eEvents
        //{
        //    e_return_msg = 0,
        //    e_order_return_msg,
        //    e_get_trades,
        //    e_get_user_info,
        //    e_get_server_time,

        //    e_total
        //}
        //ManualResetEvent[] ArrEvents = null;
        //AutoResetEvent EventRequestDone = new AutoResetEvent(false);

        //
        //SReturnCode ReturnCode;
        //SOrderReturnCode OrderReturnCode;
        //SGetTradesReturn GetTradesReturn;
        //List<STradesAdd> ListTradesAdd = new List<STradesAdd>();
        //SGetUserInfo GetUserInfo;
        //SGetServerTime GetServerTime;

        class Response
        {
            public Response(SBase Obj)
            {
                _event = new AutoResetEvent(false);
                _response = Obj;
            }

            public Response(SBase Obj, AutoResetEvent Event)
            {
                _event = Event;
                _response = Obj;
            }

            public AutoResetEvent _event = null;
            public SBase _response = null;
            public List<STradesAdd> _trades = null;
        }
        ConcurrentDictionary<long, Response> MapMessages = new ConcurrentDictionary<long, Response>();
    }
}
