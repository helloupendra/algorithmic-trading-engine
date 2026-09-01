using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Auth
{
    public class LoginRequest
    {
        public string UserNameOrEmail { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
