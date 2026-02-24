using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class DispatchResult
    {
        public int code { get; set; }
        public List<DataServer> data { get; set; }
    }

    public class DataServer
    {
        public string ip { get; set; }
        public string domain { get; set; }
    }
}
