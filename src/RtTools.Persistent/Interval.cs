using System;
using System.Collections.Generic;

namespace RtTools
{
    /// <summary>
    /// Predefined Int16 Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class Int16Interval(Int16 start, Int16 end) : Interval<Int16>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<short> Clone => new Int16Interval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Int16 Addition(Int16 x, Int16 y) => (Int16)(x + y);

        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Int16 Subtraction(Int16 x, Int16 y) => (Int16)(x - y);

    }
    /// <summary>
    /// Predefined Int32 Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class Int32Interval(Int32 start, Int32 end) : Interval<Int32>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<Int32> Clone => new Int32Interval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Int32 Addition(Int32 x, Int32 y) => x + y;
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Int32 Subtraction(Int32 x, Int32 y) => x - y;
    }
    /// <summary>
    /// Predefined Int64 Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class Int64Interval(Int64 start, Int64 end) : Interval<Int64>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<Int64> Clone => new Int64Interval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Int64 Addition(Int64 x, Int64 y) => x + y;
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Int64 Subtraction(Int64 x, Int64 y) => x - y;
    }
    /// <summary>
    /// Predefined UInt16 Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class UInt16Interval(UInt16 start, UInt16 end) : Interval<UInt16>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<UInt16> Clone => new UInt16Interval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override UInt16 Addition(UInt16 x, UInt16 y) => (UInt16)(x + y);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override UInt16 Subtraction(UInt16 x, UInt16 y) => (UInt16)(x - y);
    }
    /// <summary>
    /// Predefined UInt32 Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class UInt32Interval(UInt32 start, UInt32 end) : Interval<UInt32>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<UInt32> Clone => new UInt32Interval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override UInt32 Addition(UInt32 x, UInt32 y) => x + y;
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override UInt32 Subtraction(UInt32 x, UInt32 y) => x - y;
    }
    /// <summary>
    /// Predefined UInt64 Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class UInt64Interval(UInt64 start, UInt64 end) : Interval<UInt64>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<UInt64> Clone => new UInt64Interval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override UInt64 Addition(UInt64 x, UInt64 y) => x + y;
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override UInt64 Subtraction(UInt64 x, UInt64 y) => x - y;
    }
    /// <summary>
    /// Predefined Single Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class SingleInterval(Single start, Single end) : Interval<Single>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<Single> Clone => new SingleInterval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Single Addition(Single x, Single y) => x + y;
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Single Subtraction(Single x, Single y) => x - y;
    }
    /// <summary>
    /// Predefined Double Range
    /// </summary>
    /// <remarks>
    /// constructor
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class DoubleInterval(Double start, Double end) : Interval<Double>(start, end)
    {
        /// <summary>
        /// overridden
        /// </summary>
        public override Interval<Double> Clone => new DoubleInterval(this.Start, this.End);
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Double Addition(Double x, Double y) => x + y;
        /// <summary>
        /// overridden
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected override Double Subtraction(Double x, Double y) => x - y;
    }

    /// <summary>
    /// Structure From - To records
    /// </summary>
    /// <remarks>
    /// constructs new range
    /// </remarks>
    /// <param name="start"></param>
    /// <param name="end"></param>
    public abstract class Interval<T>(T start, T end) : IEquatable<Interval<T>>, IComparable<Interval<T>> where T: struct, IComparable<T>
    {
        /// <summary>
        /// Range start
        /// </summary>
        public T Start { get; set; } = start;
        /// <summary>
        /// Range end
        /// </summary>
        public T End { get; set; } = end;
        /// <summary>
        /// Range count (between Start and End)
        /// </summary>
        public T Count {
            get => this.Subtraction(this.End, this.Start);
            set => this.End = this.Addition(this.Start, value);
        }
        //protected virtual T Epsilon { get { return default(T); } }
        /// <summary>
        /// Addition method
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        abstract protected T Addition(T x, T y);
        /// <summary>
        /// Subtraction method
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        abstract protected T Subtraction(T x, T y);

        /// <summary>
        /// Clone range
        /// </summary>
        public virtual Interval<T> Clone => (Interval<T>)this.MemberwiseClone();

        /// <summary>
        /// overridden
        /// </summary>
        /// <returns></returns>
        public override string ToString() => this.Start + "-" + this.End;

        /// <summary>
        /// Unions 2 ranges
        /// </summary>
        /// <param name="range"></param>
        /// <returns></returns>
        public bool Union(Interval<T> range)
        {
            bool ret = false;
            if (range.Start.CompareTo(this.Start) < 0)
            {
                this.Start = range.Start;
                ret = true;
            }
            if (this.End.CompareTo(range.End) < 0)
            {
                this.End = range.End;
                ret = true;
            }
            return ret;
        }

        /// <summary>
        /// Adds range to sorted list of ranges
        /// </summary>
        /// <param name="sortedList"></param>
        /// <returns>true if list is updated at all</returns>
        public bool AddTo(List<Interval<T>> sortedList)
        {
            if (sortedList.Count == 0)
            {
                sortedList.Add(this.Clone);
                return true;
            }

            bool toInsert = true;
            bool ret = false;
            Interval<T>? lastItem = null;
            int i = this.GetFirstContactsIndex(sortedList);
            if (i < 0) i = ~i;
            for (; i < sortedList.Count; i++)
            {
                Interval<T> item = sortedList[i];
                if (this.End.CompareTo(item.Start) < 0) break; // range after start-end is found - stop cycle
                if (item.End.CompareTo(this.Start) < 0) continue; // still before from-to range
                // intersects with current item
                toInsert = false;

                if (item.Union(this)) ret = true;

                if (lastItem != null && item.Contacts(lastItem))
                { // intersects with last item
                    //if (!item.Union(lastItem))
                    item.Union(lastItem);
                    sortedList.RemoveAt(--i);
                    ret = true;
                }
                lastItem = item;
            }

            if (toInsert)
            { // non merged - should be inserted
                ret = true;
                sortedList.Insert(i, this.Clone);
            }

            return ret;
        }

        /// <summary>
        /// Returns index of first, intersecting range
        /// </summary>
        /// <param name="sortedList"></param>
        /// <returns></returns>
        public int GetFirstIntersectsIndex(List<Interval<T>> sortedList)
        {
            int index = sortedList.BinarySearch(this);
            if (index < 0) index = ~index; // equal found

            // index of first item, where search.From <= item.From
            if (index > 0 && // check back for intersection
                sortedList[index - 1].Intersects(this)) return index - 1;

            if (index >= sortedList.Count ||
                !this.Intersects(sortedList[index])) return ~index;

            return index;
        }

        /// <summary>
        /// Returns index of first, contacts range
        /// </summary>
        /// <param name="sortedList"></param>
        /// <returns></returns>
        public int GetFirstContactsIndex(List<Interval<T>> sortedList)
        {
            int index = sortedList.BinarySearch(this);
            if (index < 0) index = ~index; // equal found

            // index of first item, where search.From <= item.From
            if (index > 0 && // check back for intersection
                sortedList[index - 1].Contacts(this)) return index - 1;

            if (index >= sortedList.Count ||
                !this.Contacts(sortedList[index])) return ~index;

            return index;
        }

        /// <summary>
        /// removes from-to interval from sorted list
        /// </summary>
        /// <param name="sortedList"></param>
        public int RemoveFrom(List<Interval<T>> sortedList)
        {
            if (sortedList.Count == 0) return -1;
            int ret = this.GetFirstIntersectsIndex(sortedList);
            if (ret < 0) return ret; // if no intersection - nothing to remove
            T start = this.Start;
            T end = this.End;
            for (int i = ret; i < sortedList.Count; i++)
            {
                Interval<T> item = sortedList[i];
                if (end.CompareTo(item.Start) <= 0) break; // range after from-to is found - stop cycle
                if (item.End.CompareTo(start) <= 0) continue; // still before from-to range

                if (item.Start.CompareTo(start) < 0)
                { // item starts before from-to range
                    if (end.CompareTo(item.End) < 0)
                    { // ends after from-to range (split into 2)
                        Interval<T> toAdd = item.Clone;
                        toAdd.Start = end;
                        sortedList.Insert(i + 1, toAdd);
                    }
                    item.End = start;
                }
                else
                { // if (scan.From >= from) { // item starts after from-to range
                    if (end.CompareTo(item.End) < 0)
                    { // ends after from-to range
                        item.Start = end;
                    }
                    else
                    { // starts after, ends before from-to range (so is fully included - remove)
                        sortedList.RemoveAt(i); i--;
                    }
                }
            }
            return ret;
        }

        /// <summary>
        /// Returns removed partition from list
        /// </summary>
        /// <param name="sortedList"></param>
        /// <returns></returns>
        public List<Interval<T>> Remove(IEnumerable<Interval<T>> sortedList)
        {
            List<Interval<T>> ret = [this.Clone];
            foreach (Interval<T> item in sortedList)
                item.RemoveFrom(ret);
            return ret;
        }

        /// <summary>
        /// Remove ranges from sorted list
        /// </summary>
        /// <param name="sortedList"></param>
        /// <param name="ranges"></param>
        /// <returns></returns>
        public static int Remove(List<Interval<T>> sortedList, params Interval<T>[] ranges)
        {
            int count = 0;
            foreach (Interval<T> range in ranges)
                if (range.RemoveFrom(sortedList) >= 0) ++count;
            return count;
        }

        /// <summary>
        /// Remove ranges from sorted list
        /// </summary>
        /// <param name="sortedList"></param>
        /// <param name="ranges"></param>
        /// <returns></returns>
        public static int Remove(List<Interval<T>> sortedList, IEnumerable<Interval<T>> ranges)
        {
            int count = 0;
            foreach (Interval<T> range in ranges)
                if (range.RemoveFrom(sortedList) >= 0) ++count;
            return count;
        }

        /// <summary>
        /// Return intersected part between current range and another one
        /// </summary>
        /// <param name="range"></param>
        /// <param name="ret"></param>
        /// <returns></returns>
        public bool Intersection(Interval<T> range, out Interval<T> ret)
        {
            ret = this.Clone;
            if (0 < range.Start.CompareTo(this.Start))
                ret.Start = range.Start;
            if (0 < this.End.CompareTo(range.End))
                ret.End = range.End;
            return ret.Start.CompareTo(ret.End) <= 0;
        }

        /// <summary>
        /// Returns list intersection with current range
        /// </summary>
        /// <param name="sortedList"></param>
        /// <returns></returns>
        public IEnumerable<Interval<T>> Intersection(List<Interval<T>> sortedList)
        {
            int index, count;
            if ((count = sortedList.Count) > 0 && (index = this.GetFirstIntersectsIndex(sortedList)) >= 0)
                while (0 <= index && index < count && this.Intersection(sortedList[index++], out Interval<T> ret))
                    yield return ret;
        }

        /// <summary>
        /// Get if interval intersects sorted list
        /// </summary>
        /// <param name="sortedList"></param>
        /// <returns></returns>
        public bool Intersects(List<Interval<T>> sortedList) => this.GetFirstIntersectsIndex(sortedList) >= 0;

        /// <summary>
        /// Get if interval is fully contained in some element
        /// </summary>
        /// <param name="sortedList"></param>
        /// <returns></returns>
        public int GetContainingIndex(List<Interval<T>> sortedList)
        {
            int count = sortedList.Count;
            if (count > 0)
            {
                int i = this.GetFirstIntersectsIndex(sortedList);
                if (i < 0) i = ~i;
                for (; i < count; i++)
                {
                    Interval<T> item = sortedList[i];
                    if (item.Start.CompareTo(this.Start) > 0) break; // range after from-to - not found
                    if (this.End.CompareTo(item.End) <= 0) // item.From <= from, to <= item.To - means containing
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Get if two intervals intersects
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public bool Intersects(T start, T end) => this.Start.CompareTo(end) < 0 && start.CompareTo(this.End) < 0;

        /// <summary>
        /// Contacts = Intersects or is side-by-side to (0-80)(80-100) does not intersects, but contacts
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public bool Contacts(T start, T end) => this.Start.CompareTo(end) <= 0 && start.CompareTo(this.End) <= 0;

        /// <summary>
        /// Get if two intervals intersects
        /// </summary>
        /// <param name="range">range to check</param>
        /// <returns></returns>
        public bool Intersects(Interval<T> range) => this.Start.CompareTo(range.End) < 0 && range.Start.CompareTo(this.End) < 0;

        /// <summary>
        /// Contacts = Intersects or is side-by-side to (0-80)(80-100) does not intersects, but contacts
        /// </summary>
        /// <param name="range">range to check</param>
        /// <returns></returns>
        public bool Contacts(Interval<T> range) => this.Start.CompareTo(range.End) <= 0 && range.Start.CompareTo(this.End) <= 0;

        /// <summary>
        /// Get if item is inside range
        /// </summary>
        /// <param name="item">item to check</param>
        /// <returns></returns>
        public bool Contains(T item) => this.Start.CompareTo(item) <= 0 && 0 < this.End.CompareTo(item);

        /// <summary>
        /// Get if two intervals intersects
        /// </summary>
        /// <param name="range">range to check</param>
        /// <returns></returns>
        public bool Contains(Interval<T> range) => this.Start.CompareTo(range.Start) <= 0 && range.End.CompareTo(this.End) <= 0;

        /// <summary>
        /// Check 2 ranges for equality
        /// </summary>
        /// <param name="other">range to check</param>
        /// <returns></returns>
        public bool Equals(Interval<T>? other) => other is not null && this.Start.CompareTo(other.Start) == 0 && this.End.CompareTo(other.End) == 0;

        /// <summary>
        /// Compares two ranges
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(Interval<T>? other)
        {
            if (other is null)
                return 1;

            int ret = this.Start.CompareTo(other.Start);
            return ret == 0 ? this.End.CompareTo(other.End) : ret;
        }
    }
}
