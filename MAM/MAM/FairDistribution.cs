using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MAM
{
    public static class FairDistribution
    {
        public static bool Distribute(bool isDividePartial, int totalAmount, ref List<FairItem> items)
        {
            int totalResult = 0;

            //loop the items and set each item result to it's rounded amount (by it's ratio) and count the leftovers
            foreach (FairItem item in items)
            {
                if (isDividePartial)
                {
                    item.Ratio = Math.Abs(item.Ratio);
                }
                item.RawAmount = totalAmount * item.Ratio;
                item.Result = Convert.ToInt32(Math.Floor(item.RawAmount));
                item.LeftOver = item.RawAmount - item.Result;
                totalResult += item.Result;
            }
            if (isDividePartial)
            {
                return true;
            }
            //sort the items so the ones with the biggest leftover will be first (if 2 items have the same leftover let the biggest ration be the first)
            items.Sort(delegate(FairItem i1, FairItem i2)
            {
                if (i1.LeftOver == i2.LeftOver)
                    return i2.Ratio.CompareTo(i1.Ratio);
                else
                    return i2.LeftOver.CompareTo(i1.LeftOver);
            });

            //now spread the leftovers fairly
            if (totalResult < totalAmount)
            {
                for (int i = 0; i < totalAmount - totalResult; i++)
                {
                    items[i].Result += 1;
                }
            }
            return true;
        }

        public static void PartialCloseDistribute(int managerCloseVolume, double closeRatio, ref List<FairItem> items)
        {
            double totalCloseVolume = 0;
            //loop the items and set each item result to it's rounded amount (by it's ratio) and count the leftovers
            foreach (FairItem item in items)
            {
                item.RawAmount = item.Result * closeRatio;
                item.Result = (int)(Math.Floor(item.RawAmount));
                item.LeftOver = item.RawAmount - item.Result;
                totalCloseVolume += item.Result;
            }

            //sort the items so the ones with the biggest leftover will be first (if 2 items have the same leftover let the biggest ration be the first)
            items.Sort(delegate(FairItem i1, FairItem i2)
            {
                if (i1.LeftOver == i2.LeftOver)
                    return i2.Ratio.CompareTo(i1.Ratio);
                else
                    return i2.LeftOver.CompareTo(i1.LeftOver);
            });

            if (totalCloseVolume < managerCloseVolume)
            {
                for (int i = 0; i < managerCloseVolume - totalCloseVolume; i++)
                {
                    items[i].Result += 1;
                }
            }
        }
    }

    public class FairItem
    {
        public int ID { get; set; } 
        public double Ratio { get; set; }
        public double RawAmount { get; set; }
        public double LeftOver { get; set; }
        public int Result { get; set; }
    }
}
