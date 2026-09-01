using AlgoTrading.Contracts.Auth;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AlgoTrading.Application.Interfaces
{
    public interface IAuthService
    {
        Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

        Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

        Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

        Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default);

        Task<MeResponse?> GetMeAsync(long userId, CancellationToken cancellationToken = default);

        Task<List<MeResponse>> GetAllUsersAsync(CancellationToken cancellationToken = default);

        Task<MeResponse?> GetUserByIdAsync(long userId, CancellationToken cancellationToken = default);

        Task<bool> DeleteUserByUsernameAsync(string username, CancellationToken cancellationToken = default);
    }
}
