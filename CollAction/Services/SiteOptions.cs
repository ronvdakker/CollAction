﻿using System;
using System.ComponentModel.DataAnnotations;

namespace CollAction.Services
{
    public sealed class SiteOptions
    {
        [Required]
        public string PublicAddress { get; set; } = null!;

        [Required]
        public string AllowedCorsOrigins {get; set;} = null!;

        public Uri PublicUrl
            => new Uri(PublicAddress);
    }
}
