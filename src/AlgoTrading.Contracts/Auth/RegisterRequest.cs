using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Auth
{
    public class RegisterRequest
    {
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
