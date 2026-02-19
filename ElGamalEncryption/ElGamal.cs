using ElGamalEncryption;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Formatters;
using System.Text;
using System.Threading.Tasks;

namespace ElGamalEncryption
{
    public class ElGamal
    {
        public int P { get; set; }
        public int G { get; set; }
        public int X { get; set; }
        public int K { get; set; }
        public int Y { get; set; }

        private int A { get; set; }

        public void InitializeElGamal(int p, int g, int x, int k)
        {
            this.P = p;
            this.G = g;
            this.X = x;
            this.K = k;

            this.Y = (int)MathTools.FastExp(g, x, p);
            this.A = (int)MathTools.FastExp(g, k, p);
        }

        public byte[] StartEncryption(byte[] srcData)
        {
            int[] result = new int[srcData.Length * 2];

            for (int i = 0; i < srcData.Length; i++)
            {
                if (srcData[i] >= this.P)
                {
                    return null;
                }

                result[2 * i] = (int)this.A;
                result[2 * i + 1] = (int)MathTools.FastPowMul(this.Y, this.K, srcData[i], this.P);
            }

            byte[] byteResult = new byte[result.Length * sizeof(int)];
            Buffer.BlockCopy(result, 0, byteResult, 0, byteResult.Length);

            return byteResult;
        }

        public byte[] StartDecryption(byte[] srcData)
        {
            if (srcData.Length % sizeof(int) != 0)
            {
                return null;
            }

            int[] intArray = new int[srcData.Length / sizeof(int)];
            Buffer.BlockCopy(srcData, 0, intArray, 0, srcData.Length);

            byte[] result = new byte[srcData.Length / 8];

            for (int i = 0; i < intArray.Length / 2; i++)
            {
                int a = intArray[2 * i];
                int b = intArray[2 * i + 1];

                result[i] = (byte)MathTools.FastPowMul(a, -this.X, b, this.P);
            }

            return result;
        }

        public List<int> GetPrimitiveRoots(int p)
        {
            List<int> roots = new List<int>();
            int eilerFuncValue = p - 1;

            for (int g = 2; g < eilerFuncValue; g++)
            {
                long modulo = MathTools.FastExp(g, eilerFuncValue, p);
                if (modulo == 1)
                {
                    bool isPrimitiveRoot = true;
                    for (int l = 1; l <= eilerFuncValue - 1; l++)
                    {
                        long otherModulo = MathTools.FastExp(g, l, p);
                        if (otherModulo == 1)
                        {
                            isPrimitiveRoot = false;
                            break;
                        }
                    }
                    if (isPrimitiveRoot)
                    {
                        roots.Add(g);
                    }
                }
            }

            return roots;
        }


        public List<int> GetPrimitiveRots(int p)
        {
            List<int> roots = new List<int>();
            int eilerFuncValue = p - 1;

            List<int> primeNumbers = GetUniquePrimeFactors(p - 1);
            List<int> degrees = new List<int>();
            for (int i = 0; i < primeNumbers.Count; i++)
            {
                degrees.Add((p - 1) / primeNumbers[i]);
            }

            for (int g = 2; g < eilerFuncValue; g++)
            {
                bool isPrimitiveRoot = true;
                foreach (int degree in degrees)
                {
                    long modulo = MathTools.FastExp(g, degree, p);
                    if (modulo == 1)
                    {
                        isPrimitiveRoot = false;
                        break;
                    }
                }

                if (isPrimitiveRoot) roots.Add(g);
            }

            return roots;
        }
        public static List<int> GetUniquePrimeFactors(int n)
        {
            List<int> factors = new List<int>();

            if (n < 2)
                return factors; 

            if (n % 2 == 0)
            {
                factors.Add(2);
                while (n % 2 == 0)
                    n /= 2;
            }

            for (int i = 3; i * i <= n; i += 2)
            {
                if (n % i == 0)
                {
                    factors.Add(i);
                    while (n % i == 0)
                        n /= i;
                }
            }

            if (n > 1)
                factors.Add(n);

            return factors;
        }


        public List<int> GetListK(int p)
        {
            List<int> kList = new List<int>();
            for (int k = 2; k < p - 1; k++)
            {
                if (MathTools.IsRelativelyPrime(k, p - 1))
                {
                    kList.Add(k);
                }
            }

            return kList;
        }
    }
}