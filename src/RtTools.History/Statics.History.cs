#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
namespace RtTools { public static partial class Statics {
        /// <summary>
        /// Merge 2 sorted arrays
        /// </summary>
        /// <typeparam name="K"></typeparam>
        /// <param name="index0">first sorted array</param>
        /// <param name="start0">start index in index0</param>
        /// <param name="length0">intex0 length</param>
        /// <param name="index1">second sorted array</param>
        /// <param name="start1">start in index1</param>
        /// <param name="length1">index1 length</param>
        /// <param name="result">result array</param>
        /// <param name="start">result array start</param>
        /// <param name="comparer"></param>
        /// <returns></returns>
        public static void MergeSorted<K>(K[] index0, int start0, int length0, K[] index1, int start1, int length1, K[] result, int start, IComparer<K>? comparer = null)
        {
            comparer ??= Comparer<K>.Default;
            int end0 = start0 + length0;
            int end1 = start1 + length1;
            int i0 = start0;
            int i1 = start1;
            int iR = start;

            while (i0 < end0 && i1 < end1)
            {
                if (comparer.Compare(index0[i0], index1[i1]) <= 0)
                    result[iR++] = index0[i0++];
                else
                    result[iR++] = index1[i1++];
            }

            if (i0 < end0)
                Array.Copy(index0, i0, result, iR, end0 - i0);
            else if (i1 < end1)
                Array.Copy(index1, i1, result, iR, end1 - i1);
        }

        /// <summary>
        /// Merge 2 sorted arrays
        /// </summary>
        /// <typeparam name="K"></typeparam>
        /// <param name="index0"></param>
        /// <param name="index1"></param>
        /// <param name="comparer"></param>
        /// <returns></returns>
        public static K[] MergeSorted<K>(K[] index0, K[] index1, IComparer<K>? comparer = null)
        {
            int count0 = index0.Length;
            if (count0 == 0) return index1;
            int count1 = index1.Length;
            if (count1 == 0) return index0;

            K[] ret = new K[count0 + count1];
            MergeSorted(index0, 0, count0, index1, 0, count1, ret, 0, comparer);
            return ret;
        }

        /// <summary>
        /// Merge 2 sorted arrays
        /// </summary>
        /// <typeparam name="K"></typeparam>
        /// <param name="index0"></param>
        /// <param name="index1"></param>
        /// <returns>sorted array</returns>
        public static K[] MergeSorted<K>(K[] index0, K[] index1) where K : IComparable<K>
        {
            int count0 = index0.Length;
            if (count0 == 0) return index1;
            int count1 = index1.Length;
            if (count1 == 0) return index0;

            K[] ret = new K[count0 + count1];
            int i0 = 0;
            int i1 = 0;
            int iR = 0;

            while (i0 < count0 && i1 < count1)
            {
                if (index0[i0].CompareTo(index1[i1]) <= 0)
                    ret[iR++] = index0[i0++];
                else
                    ret[iR++] = index1[i1++];
            }

            if (i0 < count0)
                Array.Copy(index0, i0, ret, iR, count0 - i0);
            else if (i1 < count1)
                Array.Copy(index1, i1, ret, iR, count1 - i1);

            return ret;
        }

        /// <summary>
        /// Merges two sorted Lists into sorted array
        /// </summary>
        /// <typeparam name="K"></typeparam>
        /// <param name="index0">first array to merge</param>
        /// <param name="index1">second array to merge</param>
        /// <returns>sorted array, combination ot both</returns>
        public static K[] MergeSorted<K>(List<K> index0, List<K> index1) where K : IComparable<K>
        {
            int count0 = index0.Count;
            if (count0 == 0) return index1.ToArray();
            int count1 = index1.Count;
            if (count1 == 0) return index0.ToArray();

            K[] ret = new K[count0 + count1];
            int i0 = 0;
            int i1 = 0;
            int iR = 0;

            while (i0 < count0 && i1 < count1)
            {
                if (index0[i0].CompareTo(index1[i1]) <= 0)
                    ret[iR++] = index0[i0++];
                else
                    ret[iR++] = index1[i1++];
            }

            if (i0 < count0)
                index0.CopyTo(i0, ret, iR, count0 - i0);
            else if (i1 < count1)
                index1.CopyTo(i1, ret, iR, count1 - i1);

            return ret;
        }

        /// <summary>
        /// Read all stream
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static byte[] ReadToEnd(Stream stream)
        {
            using var ms = stream.CanSeek
                ? new MemoryStream((int)Math.Max(0, stream.Length - stream.Position))
                : new MemoryStream();

            stream.CopyTo(ms);
            return ms.ToArray();
        }

} }
