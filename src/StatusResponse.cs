using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class StatusResponse
    {
        public string status { get; set; } = default!;
        public DateTime timestamp { get; set; }
    }
}