
using ClienteSGR.Models;
using MessagePack;
using System.Collections.Generic;

namespace ClienteSGR.Models
{
    [MessagePackObject]
    public class DataBatchContainer
    {
        [Key(0)]
        public List<DataBatch> Batches { get; set; } = new List<DataBatch>();
    }
}