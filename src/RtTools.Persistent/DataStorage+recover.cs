using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RtTools
{
    partial class DataStorage
    {

        /// <summary>
        /// Data storage exception.
        /// </summary>
        public class DataSourceException : Exception
        {
            public DataSourceException(string? message = null, Exception? exception = null) : base(message, exception) { }

            public DataSourceException() { }

            public DataSourceException(string message) : base(message) { }
        }

        [Flags]
        public enum OnException
        {
            /// <summary>
            /// Default action = 
            /// </summary>
            Default = 0,

            /// <summary>
            /// Close input stream
            /// </summary>
            CloseSource = 1,

            /// <summary>
            /// Close stream and throw exception
            /// </summary>
            CloseAndThrow = CloseSource | Throw,

            /// <summary>
            /// recover Header Size
            /// </summary>
            Recover = 4,

            /// <summary>
            /// recover Header Size
            /// </summary>
            RecoverOrThrow = Recover | CloseAndThrow,

            /// <summary>
            /// Delete Data and create new file
            /// </summary>
            Clear = 8,

            /// <summary>
            /// Try To Recover - if not - clear all data and continue
            /// </summary>
            RecoverOrClear = Recover | Clear,

            /// <summary>
            /// Make Consistency scan on opening
            /// </summary>
            CheckConsistency = 0x1000,

            /// <summary>
            /// Just throw
            /// </summary>
            Throw = 0x8000,
        }

        [Flags]
        public enum OpenState
        {
            /// <summary>
            /// Initial state (no listener)
            /// </summary>
            Default = 0,
            /// <summary>
            /// Initial scan failed
            /// </summary>
            Failed = 1,
            /// <summary>
            /// Recovered (After fail)
            /// </summary>
            Recovered = 2 | Failed,
            /// <summary>
            /// Failed and cleared
            /// </summary>
            Cleared = 8 | Failed,

            /// <summary>
            /// Failed and Exception is going to be thrown
            /// </summary>
            Throw = 0x40 | Failed,

            /// <summary>
            /// Failed and closed (not success) - incompatible with <see cref="Opened"/>
            /// </summary>
            Closed = 0x80 | Failed,

            /// <summary>
            /// Finally opened (incompatible with <see cref="Closed"/>) (see other flags for history - Failed etc...)
            /// </summary>
            Opened = 0x100,
        }

        RecoveryChain? scanHeaders(long firstPosition, ushort headerSize, bool validateSerializedData, Predicate<object?>? dataCheck)
        {
            Debug.WriteLine(headerSize);
            long streamLength = this.ioStream.Length;
            long maxHeaderPos = streamLength - RecordHeader.HeaderSize;


            Dictionary<long, RecordHeader> headers = [];
            List<RecordHeader> sorted = [];

            BufferBuilder bb = new(RecordHeader.HeaderSize * 10000);
            long bbpos, headerPos;
            long prev, next;
            int dlen;
            bool first, last;

            const int MaxDataLength = 100_000_000; // 100 MB max data length to look for

            ///
            /// full scan on file to get valid headers


            long streamPos = this.ioStream.Seek(firstPosition, SeekOrigin.Begin);
            while (streamPos < streamLength)
            {
                bbpos = streamPos - bb.Length; // position of first byte in BufferBuilder
                int read = this.ioStream.Read(bb.Buffer, bb.Length, (int)Math.Min(bb.Capacity - bb.Length, streamLength - streamPos));
                if (read <= 0)
                    UnexpectedEnd(this.ioStream);
                bb.Length += read;

                int offset = 0, max = bb.Length - RecordHeader.HeaderSize;
                while (offset < max)
                {
                    headerPos = bbpos + (bb.Offset = offset); // header position
                    prev = bb.ReadInt64();
                    if ((first = (prev == long.MinValue)) || // potential first record
                        (firstPosition <= prev && prev < maxHeaderPos))
                    {
                        /*if (!(!first && // not first 
                            prev < headerPos && // prev points to previous position
                            !headers.ContainsKey(prev)))
                        {*/
                        next = bb.ReadInt64();
                        if ((last = (next == long.MinValue)) || // potential first record
                            (firstPosition <= next && next < maxHeaderPos))

                        /*if (!(!last && // not first 
                            next < headerPos && // next points to previous position
                            !headers.ContainsKey(next)))*/
                        {
                            /*int info = */
                            bb.ReadInt32();
                            dlen = bb.ReadInt32();
                            if (0 <= dlen && dlen <= MaxDataLength && // data length should be positive and < realistic length
                                (headerPos + RecordHeader.HeaderSize + dlen) <= streamLength) // less than stream size
                            {
                                if (bb.Left < dlen)
                                { // no room for data - exit to reload header
                                    dlen += RecordHeader.HeaderSize;
                                    if (bb.Capacity < dlen) // no room to put data into
                                        bb.Capacity = dlen;
                                    break; // break to load next chunk data
                                }
                                try
                                {
                                    byte[] data = bb.ReadArray<byte>(dlen, 1) ?? throw new DataSourceException("Unable to read serialized data.");
                                    object? item = validateSerializedData || dataCheck != null ? this.FromBytes(data) : null;
                                    if (dataCheck?.Invoke(item) ?? true)
                                    { // data is OK
                                        RecordHeader header = new() { Position = headerPos, Prev = prev, Next = next, DataLength = dlen };
                                        sorted.Add(header);
                                        headers.Add(headerPos, header);
                                    }
                                }
                                catch (Exception dataException)
                                {
                                    Debug.WriteLine(dataException);
                                }
                            }
                        }
                        //}
                    }
                    offset++;
                }
                bb.Offset = offset;
                bb.ShiftToStart(); // shift buffer
                streamPos = this.ioStream.Position;
            }

            /// Keep just headers, where:
            ///    1. points start/end (prev==long.MinValue) or (next==long.MinValue)
            /// or 2. there is a header pointing (?.prev=header.Position && header.Next == ?.Position) current one
            /// or 3. there is a header, pointing (next)
            List<RecordHeader> potential = [.. sorted];
            while (potential.RemoveAll(s =>
                 {
                     if ((s.Prev != long.MinValue && // not marked as first
                          (!headers.TryGetValue(s.Prev, out RecordHeader h) || h.Next != s.Position)) && // previous is pointing OK
                         (s.Next != long.MinValue && // not marked as last
                         (!headers.TryGetValue(s.Next, out h) || h.Prev != s.Position))) // next is pointing this
                         return true; // headers.Remove(s.Position);

                     return false;
                 }) > 0) ;

            if (potential.Count == 0) return null;

            /// headers contains all possible data headers with possible values for prev/next/datalen
            List<RecordHeader>? chain = null;
            List<RecordHeader[]> chains = [];
            RecordHeader hLast = default;
            bool hasLast = false;
            foreach (RecordHeader header in potential)
            {
                if (!hasLast)
                { // first header - just create a chain
                    chain = [header];
                }
                else if (chain != null && hLast.Next == header.Position && // last header pointing current
                    header.Prev == hLast.Position && // current header pointing last
                    (hLast.Position + hLast.RecordLength) <= header.Position) // header was not skipped by previous
                {
                    chain.Add(header);
                }
                else
                { // not chained header - store chain and start another one
                    if (chain == null)
                        throw new DataSourceException("Invalid recovery chain state.");

                    chains.Add(chain.ToArray());

                    chain.Clear();
                    chain.Add(header);
                }
                hLast = header;
                hasLast = true;
            }

            if (chain == null) return null;

            // chains found
            chains.Add(chain.ToArray());

            List<RecoveryChain> combinations = [];

            for (int i = 0; i < chains.Count; i++)
            {
                RecordHeader[] ch = chains[i];
                int found = 0;

                if (0 < ch[0].Prev)
                {
                    for (int c = combinations.Count - 1; c >= 0; c--)
                    {
                        RecoveryChain started = combinations[c];
                        long space = started.SpaceTo(ch);
                        if (0 <= space)
                        {
                            found++;
                            RecoveryChain modified = new(started); // create new 
                            modified.Add(space, ch); // append current
                            combinations.Add(modified);
                        }
                    }
                }

                if (found == 0) // add as new combination
                    combinations.Add(new RecoveryChain(firstPosition, ch));
            }

            RecoveryChain? best = null;
            foreach (RecoveryChain ch in combinations)
                if (best == null) best = ch;
                else
                {
                    int res = ch.offset.CompareTo(best.offset);
                    if (res < 0)
                    { // best offset
                        best = ch;
                        continue;
                    }
                    else if (res == 0)
                    { // equal
                        res = ch.Endings.CompareTo(best.Endings);
                        if (res > 0)
                        { // more endings
                            best = ch;
                            continue;
                        }
                        else if (res == 0)
                        { // equal 
                            res = ch.itemsCount.CompareTo(best.itemsCount);
                            if (res > 0)
                            { // more items
                                best = ch;
                                continue;
                            }
                            else if (res == 0)
                            { // equal
                                res = ch.spacing.CompareTo(best.spacing);
                                if (res < 0)
                                { // less spacing
                                    best = ch;
                                    continue;
                                }
                                // else if (res == 0) { }
                            }
                        }
                    }
                }
            return best;
        }

        sealed class RecoveryChain
        {
            internal readonly long offset;
            internal readonly long prev;
            public List<KeyValuePair<long, RecordHeader[]>> List { get; } = [];
            internal long spacing;
            internal long next;
            internal int itemsCount;

            internal RecordHeader Start => this.List[0].Value[0];

            public int Endings =>
                this.prev == long.MinValue
                ? (this.next == long.MinValue ? 2 : 1)
                : (this.next == long.MinValue ? 1 : 0);

            public RecoveryChain(RecoveryChain copy)
            {
                this.offset = copy.offset;
                this.prev = copy.prev;
                this.List = [.. copy.List];
                this.spacing = copy.spacing;
                this.next = copy.next;
                this.itemsCount = copy.itemsCount;
            }

            public RecoveryChain(long firstPosition, RecordHeader[] ch)
            {
                RecordHeader first = ch[0];
                long space = (this.prev = first.Prev) < 0 ? 0 : this.prev;
                space += (this.offset = (first.Position - firstPosition)); // offset to first position
                this.Add(space, ch);
            }

            internal long SpaceTo(RecordHeader[] ch)
            {
                long prev;
                if (this.next < 0 || // finished
                    (prev = ch[0].Prev) < 0 || // try to add starting item
                    (prev -= this.next) <= 0) return long.MinValue; // overlapped
                return prev;
            }

            internal void Add(long space, RecordHeader[] ch)
            {
                this.spacing += space;
                this.itemsCount += ch.Length;
                this.List.Add(new KeyValuePair<long, RecordHeader[]>(space, ch));
                this.next = ch[ch.Length - 1].Next;
            }

            public override string ToString() => this.offset + ": " + this.prev + " .. " + this.next + " - " + this.itemsCount + " / " + this.spacing;
        }

        private bool TryToRecover(DataSourceException? exception, ushort headerSize, bool validateSerializedData)
        {
            long epoch = System.Threading.Volatile.Read(ref this.mutationEpoch);
            try { return this.TryToRecoverCore(exception, headerSize, validateSerializedData); }
            catch (Exception error) { this.RememberMutationFailure(epoch, error); throw; }
        }

        private bool TryToRecoverCore(DataSourceException? exception, ushort headerSize, bool validateSerializedData)
        {
            Debug.WriteLine("recover " + exception);

            RecoveryChain? found = null;
            long firstPos = this.FirstPosition;
            if (firstPos >= this.DataOffset && firstPos < this.ioStream.Length - RecordHeader.HeaderSize) // seems to be valid
                found = this.scanHeaders(firstPos, headerSize, validateSerializedData, null);

            if (found == null && firstPos != this.DataOffset) // not success - try to read from the beginning
                found = this.scanHeaders(firstPos = this.DataOffset, headerSize, validateSerializedData, null);

            if (found == null) return false; // not found

            Debug.WriteLine("try to repair: " + found);

            // try to recover data
            firstPos = found.Start.Position;
            if (firstPos != this.FirstPosition)
            { // update header start
                Debug.WriteLine("header FirstPos: " + this.FirstPosition + " -> " + firstPos);
                this.ioStream.Seek(4, SeekOrigin.Begin);
                WriteInt64LittleEndian(this.ioStream, this.FirstPosition = firstPos);
            }

            byte[] buffer = new byte[RecordHeader.HeaderSize];
            int change = 0;
            for (int i = 0, lastIndex = found.List.Count - 1; i <= lastIndex; i++)
            {
                KeyValuePair<long, RecordHeader[]> part = found.List[i];
                RecordHeader[] chained = part.Value;
                // Persist repairs in the array: first and last may be the same record.
                ref RecordHeader header = ref chained[0];

                if (i == 0)
                { // check first
                    if (header.Prev != long.MinValue)
                    { // mark first header
                        Debug.WriteLine("hStart(" + header.Position + ").Prev: " + header.Prev + " -> long.MinValue");
                        header.Prev = long.MinValue;
                        header.Write(this.ioStream, buffer);
                    }
                }
                else if (part.Key > 0)
                { // has spacing to previous spacing
                    RecordHeader[] last = found.List[i - 1].Value;
                    ref RecordHeader lastHeader = ref last[last.Length - 1];
                    change++;
                    if (lastHeader.Next != header.Position)
                    { // last is not pointing current
                        Debug.WriteLine(change + ": h(" + lastHeader.Position + ").Next: " + lastHeader.Next + " -> " + header.Position);
                        lastHeader.Next = header.Position;
                        lastHeader.Write(this.ioStream, buffer);
                    }
                    if (header.Prev != lastHeader.Position)
                    { // current is no pointing last
                        Debug.WriteLine(change + ": h(" + header.Position + ").Prev: " + header.Prev + " -> " + lastHeader.Position);
                        header.Prev = lastHeader.Position;
                        header.Write(this.ioStream, buffer);
                    }
                }

                if (i == lastIndex)
                {
                    ref RecordHeader tail = ref chained[chained.Length - 1];
                    if (tail.Next != long.MinValue)
                    { // mark last header
                        Debug.WriteLine("hEnd(" + tail.Position + ").Next: " + tail.Prev + " -> long.MinValue");
                        tail.Next = long.MinValue;
                        tail.Write(this.ioStream, buffer);
                    }
                }
            }
            return true;
        }
    }
}
