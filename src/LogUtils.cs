using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class LogUtils
    {
        public static bool enableDebug = false;

        public static void debug(string log)
        {
            if (enableDebug)
                Console.Error.WriteLine(log);
        }
    }
}