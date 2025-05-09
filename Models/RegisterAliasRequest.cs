using System.ComponentModel.DataAnnotations;
using MessagePack; 

namespace ClienteSGR.Models
{
    [MessagePackObject] 
    public class RegisterAliasRequest
    {
        [MessagePack.Key(0)] 
        public string Alias { get; set; } = string.Empty;
    }
}