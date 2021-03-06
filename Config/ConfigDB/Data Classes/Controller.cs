using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace iChen.Persistence.Server
{
	public partial class Controller
	{
		public int ID { get; set; }
		[DefaultValue(DataStore.DefaultOrgId)]
		public string OrgId { get; set; } = DataStore.DefaultOrgId;
		public float? TimeZoneOffset { get; set; }    // Version_TimeZoneOffset
		public bool IsEnabled { get; set; } = true;
		public string Name { get; set; }
		public int Type { get; set; } = 0;
		public string Version { get; set; }
		public string Model { get; set; }
		public string IP { get; set; }
		public string LockIP { get; set; }    // Version_LockIP
		public double? GeoLatitude { get; set; }
		public double? GeoLongitude { get; set; }
		public DateTime Created { get; set; } = DateTime.Now;
		public DateTime? Modified { get; set; }

		[JsonIgnore]
		public virtual ICollection<Mold> Molds { get; set; } = new HashSet<Mold>();
	}
}
