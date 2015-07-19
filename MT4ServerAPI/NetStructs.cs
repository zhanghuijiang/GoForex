using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MT4ServerAPI
{
    public class Conversions {
        public static DateTime time_t2DateTime(UInt32 date) {
            double sec = (date);
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Local).AddSeconds(sec);

        }
        public static UInt32 DateTime2time_t(DateTime date)
        {
            return (UInt32)(date - new DateTime(1970, 1, 1)).TotalSeconds;

        }
    };

    internal class RawSerializer<T>
    {
        public T RawDeserialize(byte[] rawData)
        {
            return RawDeserialize(rawData, 0);
        }

        public T RawDeserialize(byte[] rawData, int position)
        {
            int rawsize = Marshal.SizeOf(typeof(T));
            if (rawsize > rawData.Length)
                return default(T);

            IntPtr buffer = Marshal.AllocHGlobal(rawsize);
            Marshal.Copy(rawData, position, buffer, rawsize);
            T obj = (T)Marshal.PtrToStructure(buffer, typeof(T));
            Marshal.FreeHGlobal(buffer);
            return obj;
        }

        public byte[] RawSerialize(T item)
        {
            int rawSize = Marshal.SizeOf(typeof(T));
            IntPtr buffer = Marshal.AllocHGlobal(rawSize);
            Marshal.StructureToPtr(item, buffer, false);
            byte[] rawData = new byte[rawSize];
            Marshal.Copy(buffer, rawData, 0, rawSize);
            Marshal.FreeHGlobal(buffer);
            return rawData;
        }
    }
    public enum eOrderSide
    {
        BUY,
        SELL,
        UNKNOWN
    };

    public enum eReturnCode
    {
        E_InvalidReturnCode = -1,
        E_Success = 100,
        E_Failure = 101
    }

    public enum eMessageType
    {
        E_InvalidMessageType = -1,

        E_Login = 10000,
        E_OpenOrder,
        E_CloseOrder,
        E_TradesAdd,
        E_GetTrades,
        E_GetTradesReturn,
        E_CreateUser,
        E_GetUserInfo,
        E_GetServerTime,

        E_ReturnCode = 20000,
        E_OrderReturnCode
    }

    public enum eGetOrderType
    {
        E_InvalidOrderType = -1,

        E_OpenOrders = 1,
        E_CloseOrders = 2,
        E_AllOrders = 4
    }
    public enum eOrderState { TS_OPEN_NORMAL, TS_OPEN_REMAND, TS_OPEN_RESTORED, TS_CLOSED_NORMAL, TS_CLOSED_PART, TS_CLOSED_BY, TS_DELETED };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SOrderInfo
    {
        public int Ticket;
        public int Login;
        public UInt32 Time;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string Symbol;
        public eOrderSide OrderSide;
        public int Volume;
        public double Price;
        public int NewTicket; //for partial close
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Comment;
        int State; // enum { TS_OPEN_NORMAL, TS_OPEN_REMAND, TS_OPEN_RESTORED, TS_CLOSED_NORMAL, TS_CLOSED_PART, TS_CLOSED_BY, TS_DELETED };
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Group;

        public bool IsOpenOrder() { return State == (int)eOrderState.TS_OPEN_NORMAL || State == (int)eOrderState.TS_OPEN_REMAND || State == (int)eOrderState.TS_OPEN_RESTORED; }
        public bool IsCloseOrder() { return State == (int)eOrderState.TS_CLOSED_NORMAL || State == (int)eOrderState.TS_CLOSED_PART || State == (int)eOrderState.TS_CLOSED_BY; }
        public OrderInfo ToOrderInfo()
        {
            OrderInfo ret = new OrderInfo();
            ret.Ticket = Ticket;
            ret.Login = Login;
            ret.Time = Conversions.time_t2DateTime(Time);
            ret.Symbol = Symbol;
            ret.Side = (OrderSide)OrderSide;
            ret.Volume = Volume;
            ret.Price = Price;
            ret.NewTicket = NewTicket;
            ret.Comment = Comment;
            ret.Group = Group;
            return ret;
        }

    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SUserDetails
    {
        public int Login;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Group;
        public int Leverage;
        public double Balance;
        public double Credit;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SBase
    {
        public SBase(eMessageType messageType)
        {
            MessageType = messageType;
            ServerTime = 0;
            ClientTime = DateTime.Now.Ticks;
        }

        public SBase(SBase Obj)
        {
            MessageType = Obj.MessageType;
            ServerTime = Obj.ServerTime;
            ClientTime = Obj.ClientTime;
        }

        public eMessageType MessageType;
        public UInt32 ServerTime;
        public long ClientTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SReturnCode : SBase
    {
        public SReturnCode()
            : base(eMessageType.E_ReturnCode)
        {
            CalledMessageType = eMessageType.E_InvalidMessageType;
            ReturnCode = eReturnCode.E_InvalidReturnCode;
        }

        public SReturnCode(SReturnCode Obj)
            : base(Obj)
        {
            CalledMessageType = Obj.CalledMessageType;
            ReturnCode = Obj.ReturnCode;
            ErrorDescription = Obj.ErrorDescription;

        }

        public eMessageType CalledMessageType;
        public eReturnCode ReturnCode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ErrorDescription;
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SOrderReturnCode : SReturnCode
    {
        public SOrderReturnCode()
        {
            MessageType = eMessageType.E_OrderReturnCode;
        }
        public SOrderReturnCode(SOrderReturnCode Obj) 
            : base(Obj)
        {
            OrderID = Obj.OrderID;
            IsPartial = Obj.IsPartial;
        }

        public int OrderID;
        public int IsPartial;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SLogin : SBase
    {
        public SLogin()
            : base(eMessageType.E_Login)
        {
        }

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string User;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Password;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SOpenOrder : SBase
    {
        public SOpenOrder()
            : base(eMessageType.E_OpenOrder)
        {
        }
        //[MarshalAs(UnmanagedType.Struct, SizeConst = 96)]
        public SOrderInfo OrderInfo = new SOrderInfo();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SCloseOrder : SBase
    {
        public SCloseOrder()
            : base(eMessageType.E_CloseOrder)
        {
        }
        //[MarshalAs(UnmanagedType.Struct, SizeConst = 96)]
        public SOrderInfo OrderInfo = new SOrderInfo();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class STradesAdd : SBase
    {
        public STradesAdd()
            : base(eMessageType.E_TradesAdd)
        {
        }
        //[MarshalAs(UnmanagedType.Struct, SizeConst = 96)]
        public SOrderInfo OrderInfo = new SOrderInfo();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SGetTrades : SBase
    {
        public SGetTrades()
            : base(eMessageType.E_GetTrades)
        {
            From = Conversions.DateTime2time_t(new DateTime(2015, 7, 1, 0, 0, 0));
            To = Conversions.DateTime2time_t(DateTime.Now);
            login = 4;
            GetOrderType = eGetOrderType.E_AllOrders;
        }

        public SGetTrades(SGetTrades Obj)
            : base(Obj)
        {
            From = Obj.From;
            To = Obj.To;
            login = Obj.login;
            GetOrderType = Obj.GetOrderType;
        }

        public void SetFrom(DateTime from)
        {
            From = Conversions.DateTime2time_t(from);
        }

        public void SetTo(DateTime to)
        {
            To = Conversions.DateTime2time_t(to);
        }

        public UInt32 From;
        public UInt32 To;
        public int login;
        public eGetOrderType GetOrderType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Group;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SGetTradesReturn : SGetTrades
    {
        public SGetTradesReturn()
        {
            MessageType = eMessageType.E_GetTradesReturn;
            Total = 0;
        }

        public SGetTradesReturn(SGetTradesReturn Obj)
            : base(Obj)
        {
            Total = Obj.Total;
        }
        public int Total;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SCreateUser : SBase
    {
        public SCreateUser()
            : base(eMessageType.E_CreateUser)
        {
            Login = 0;
            Deposit = 0;
        }

        public int Login;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Group;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Password;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;
        public int Deposit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SGetUserInfoReq : SBase
    {
        public SGetUserInfoReq()
            : base(eMessageType.E_GetUserInfo)
        {
            Login = 0;
        }
        public int Login;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SGetUserInfo : SReturnCode
    {
        public SGetUserInfo()
        {
            MessageType = eMessageType.E_GetUserInfo;
            Login = 0;
        }

        public SGetUserInfo(SGetUserInfo Obj) : base(Obj)
        {
            Login = Obj.Login;
            Group = Obj.Group;
            Name = Obj.Name;
            Balance = Obj.Balance;
            Credit = Obj.Credit;
        }

        public UserDetails ToUserDetails()
        {
            UserDetails ret = new UserDetails();
            ret.Login = Login;
            ret.Group = Group;
            ret.Name = Name;
            ret.Balance = Balance;
            ret.Credit = Credit;
            return ret;
        }

        public int Login;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Group;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;
        public double Balance;
        public double Credit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class SGetServerTime : SBase
    {
        public SGetServerTime()
            : base(eMessageType.E_GetServerTime)
        {
        }
    }
}
