﻿using Microsoft.AspNetCore.Authorization;
using Scrabble.Shared;
using static System.Net.WebRequestMethods;
using System.Net.Http.Json;

namespace Scrabble.Shared.Auth
{
    public class AdminHandler : AuthorizationHandler<AdminRequirement>
    {

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
        {
            //foreach (var clm in context.User.Claims)
            //{
            //    Console.WriteLine(clm);
            //}

            if (!context.User.HasClaim(c => c.Type == AppEmailClaimType.ThisAppEmailClaimType))
            {
                //Console.WriteLine("No Email in claim.");
                return;
            }

            var emailAddress = context.User.FindFirst(c => c.Type == AppEmailClaimType.ThisAppEmailClaimType).Value;
            //Console.WriteLine($"Checking auth Admin for {emailAddress}");
            var playerDto = AuthCache.CachedPlayer;
            if (playerDto == null || playerDto.Email != emailAddress)
            {
                // Retrieve new player info
                try
                {
                    if (AuthCache.AuthHttpClient != null)
                    {
                        playerDto = await AuthCache.AuthHttpClient.GetFromJsonAsync<PlayerDto>($"/api/Player");
                        AuthCache.CachedPlayer = playerDto;
                    }

                }
                catch (Exception ex) { 
                    Console.WriteLine(ex.ToString());
                    return;
                }
            }

            if (playerDto != null && playerDto.IsAdmin)
            {
                context.Succeed(requirement);
            }

            return;
        }
    }
}


