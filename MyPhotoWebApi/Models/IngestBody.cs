using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace MyPhotoWebApi.Models
{
    public class IngestBody
    {
        public string IngestFolder { get; set; }
 
        [DefaultValue(true)]
        public bool Recursive { get; set; }
    }
}
