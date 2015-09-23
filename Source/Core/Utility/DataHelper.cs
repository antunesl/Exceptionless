﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Core.Authorization;
using Exceptionless.Core.Billing;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Models;

namespace Exceptionless.Core.Utility {
    public class DataHelper {
        private readonly IOrganizationRepository _organizationRepository;
        private readonly IProjectRepository _projectRepository;
        private readonly ITokenRepository _tokenRepository;
        private readonly IUserRepository _userRepository;

        public const string TEST_USER_EMAIL = "test@exceptionless.io";
        public const string TEST_USER_PASSWORD = "tester";
        public const string TEST_ORG_ID = "537650f3b77efe23a47914f3";
        public const string TEST_PROJECT_ID = "537650f3b77efe23a47914f4";
        public const string TEST_API_KEY = "LhhP1C9gijpSKCslHHCvwdSIz298twx271n1l6xw";
        public const string TEST_USER_API_KEY = "5f8aT5j0M1SdWCMOiJKCrlDNHMI38LjCH4LTWqGp";
        public const string INTERNAL_API_KEY = "Bx7JgglstPG544R34Tw9T7RlCed3OIwtYXVeyhT2";
        public const string INTERNAL_PROJECT_ID = "54b56e480ef9605a88a13153";

        public DataHelper(IOrganizationRepository organizationRepository, IProjectRepository projectRepository, IUserRepository userRepository, ITokenRepository tokenRepository) {
            _organizationRepository = organizationRepository;
            _projectRepository = projectRepository;
            _userRepository = userRepository;
            _tokenRepository = tokenRepository;
        }

        public async Task<string> CreateDefaultOrganizationAndProjectAsync(User user) {
            string organizationId = user.OrganizationIds.FirstOrDefault();
            if (!String.IsNullOrEmpty(organizationId)) {
                var defaultProject = (await _projectRepository.GetByOrganizationIdAsync(user.OrganizationIds.First(), useCache: true).AnyContext()).Documents.FirstOrDefault();
                if (defaultProject != null)
                    return defaultProject.Id;
            } else {
                var organization = new Organization {
                    Name = "Default Organization"
                };
                BillingManager.ApplyBillingPlan(organization, Settings.Current.EnableBilling ? BillingManager.FreePlan : BillingManager.UnlimitedPlan, user);
                await _organizationRepository.AddAsync(organization).AnyContext();
                organizationId = organization.Id;
            }

            var project = new Project { Name = "Default Project", OrganizationId = organizationId };
            project.NextSummaryEndOfDayTicks = DateTime.UtcNow.Date.AddDays(1).AddHours(1).Ticks;
            project.AddDefaultOwnerNotificationSettings(user.Id);
            project = await _projectRepository.AddAsync(project).AnyContext();
            
            await _tokenRepository.AddAsync(new Token {
                Id = StringExtensions.GetNewToken(),
                OrganizationId = organizationId,
                ProjectId = project.Id,
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                Type = TokenType.Access
            }).AnyContext();

            if (!user.OrganizationIds.Contains(organizationId)) {
                user.OrganizationIds.Add(organizationId);
                await _userRepository.SaveAsync(user, true).AnyContext();
            }

            return project.Id;
        }

        public async Task CreateTestDataAsync() {
            if (await _userRepository.GetByEmailAddressAsync(TEST_USER_EMAIL).AnyContext() != null)
                return;

            var user = new User {
                FullName = "Test User", 
                EmailAddress = TEST_USER_EMAIL,
                IsEmailAddressVerified = true
            };
            user.Roles.Add(AuthorizationRoles.Client);
            user.Roles.Add(AuthorizationRoles.User);
            user.Roles.Add(AuthorizationRoles.GlobalAdmin);

            user.Salt = StringExtensions.GetRandomString(16);
            user.Password = TEST_USER_PASSWORD.ToSaltedHash(user.Salt);

            user = await _userRepository.AddAsync(user).AnyContext();
            await CreateTestOrganizationAndProjectAsync(user.Id).AnyContext();
            await CreateTestInternalOrganizationAndProjectAsync(user.Id).AnyContext();
        }

        public async Task CreateTestOrganizationAndProjectAsync(string userId) {
            if (await _tokenRepository.GetByIdAsync(TEST_API_KEY).AnyContext() != null)
                return;

            User user = await _userRepository.GetByIdAsync(userId, true).AnyContext();
            var organization = new Organization { Id = TEST_ORG_ID, Name = "Acme" };
            BillingManager.ApplyBillingPlan(organization, BillingManager.UnlimitedPlan, user);
            organization = await _organizationRepository.AddAsync(organization).AnyContext();

            var project = new Project { Id = TEST_PROJECT_ID, Name = "Disintegrating Pistol", OrganizationId = organization.Id };
            project.NextSummaryEndOfDayTicks = DateTime.UtcNow.Date.AddDays(1).AddHours(1).Ticks;
            project.Configuration.Settings.Add("IncludeConditionalData", "true");
            project.AddDefaultOwnerNotificationSettings(userId);
            project = await _projectRepository.AddAsync(project, true).AnyContext();

            await _tokenRepository.AddAsync(new Token {
                Id = TEST_API_KEY,
                OrganizationId = organization.Id,
                ProjectId = project.Id,
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                Type = TokenType.Access
            }).AnyContext();

            await _tokenRepository.AddAsync(new Token {
                Id = TEST_USER_API_KEY,
                UserId = user.Id,
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                Type = TokenType.Access
            }).AnyContext();

            user.OrganizationIds.Add(organization.Id);
            await _userRepository.SaveAsync(user, true).AnyContext();
        }

        public async Task CreateTestInternalOrganizationAndProjectAsync(string userId) {
            if (await _tokenRepository.GetByIdAsync(INTERNAL_API_KEY).AnyContext() != null)
                return;

            User user = await _userRepository.GetByIdAsync(userId, true).AnyContext();
            var organization = new Organization { Name = "Exceptionless" };
            BillingManager.ApplyBillingPlan(organization, BillingManager.UnlimitedPlan, user);
            organization = await _organizationRepository.AddAsync(organization).AnyContext();

            var project = new Project { Id = INTERNAL_PROJECT_ID, Name = "API", OrganizationId = organization.Id };
            project.NextSummaryEndOfDayTicks = DateTime.UtcNow.Date.AddDays(1).AddHours(1).Ticks;
            project.AddDefaultOwnerNotificationSettings(userId);
            project = await _projectRepository.AddAsync(project, true).AnyContext();

            await _tokenRepository.AddAsync(new Token {
                Id = INTERNAL_API_KEY,
                OrganizationId = organization.Id,
                ProjectId = project.Id,
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                Type = TokenType.Access
            }).AnyContext();

            user.OrganizationIds.Add(organization.Id);
            await _userRepository.SaveAsync(user, true).AnyContext();
        }
    }
}