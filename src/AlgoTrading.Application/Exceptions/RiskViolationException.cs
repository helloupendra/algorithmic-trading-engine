// src/AlgoTrading.Application/Exceptions/RiskViolationException.cs
using System;

namespace AlgoTrading.Application.Exceptions;

public class RiskViolationException : Exception
{
    public RiskViolationException(string message) : base(message)
    {
    }
}
