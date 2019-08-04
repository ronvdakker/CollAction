using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using CollAction.Services.Newsletter;
using System;
using Moq;
using Hangfire;
using MailChimp.Net.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CollAction.Tests.Integration
{
    [TestClass]
    [TestCategory("Integration")]
    public sealed class NewsletterServiceTests : IntegrationTestBase
    {
        [TestMethod]
        public Task TestGetListMemberStatusOnNonExistentMember()
            => WithServiceProvider(
                ConfigureReplacementServices,
                async scope =>
                {
                    var newsletterService = scope.ServiceProvider.GetRequiredService<INewsletterService>();
                    string email = GetTestEmail();
                    Assert.IsFalse(await newsletterService.IsSubscribedAsync(email));
                });

        [TestMethod]
        public Task TestSubscribeListMemberAsPending()
            => WithServiceProvider(
                   ConfigureReplacementServices,
                   async scope =>
                   {
                       var newsletterService = scope.ServiceProvider.GetRequiredService<INewsletterService>();
                       string email = GetTestEmail();

                       try
                       {
                           await newsletterService.SetSubscription(email, true, true);
                           Status status = await newsletterService.GetListMemberStatus(email);
                           Assert.AreEqual(Status.Pending, status);
                           Assert.IsTrue(await newsletterService.IsSubscribedAsync(email));
                       }
                       finally
                       {
                           await newsletterService.UnsubscribeMember(email);
                       }
                   });

        [TestMethod]
        public Task TestSubscribeListMemberAsSubscribed()
            => WithServiceProvider(
                   ConfigureReplacementServices,
                   async scope =>
                   {
                       var newsletterService = scope.ServiceProvider.GetRequiredService<INewsletterService>();
                       string email = GetTestEmail();

                       try
                       {
                           await newsletterService.SetSubscription(email, true, false);
                           Status status = await newsletterService.GetListMemberStatus(email);
                           Assert.AreEqual(Status.Subscribed, status);
                           Assert.IsTrue(await newsletterService.IsSubscribedAsync(email));
                       }
                       finally
                       {
                           await newsletterService.UnsubscribeMember(email);
                       }
                   });

        [TestMethod]
        public Task TestUnsubscribeExistingListMember()
            => WithServiceProvider(
                   ConfigureReplacementServices,
                   async scope =>
                   {
                       var newsletterService = scope.ServiceProvider.GetRequiredService<INewsletterService>();
                       string email = GetTestEmail();

                       try
                       {
                           await newsletterService.SetSubscription(email, true, true);
                           Status status = await newsletterService.GetListMemberStatus(email);
                           Assert.AreEqual(Status.Pending, status);
   
                           await newsletterService.SetSubscription(email, false, false);
                           Assert.IsFalse(await newsletterService.IsSubscribedAsync(email));
                       }
                       finally
                       {
                           await newsletterService.SetSubscription(email, false, false);
                       }
                   });

        [TestMethod]
        public Task TestUnsubscribeSubscribeMultiple()
            => WithServiceProvider(
                   ConfigureReplacementServices,
                   async scope =>
                   {
                       var newsletterService = scope.ServiceProvider.GetRequiredService<INewsletterService>();
                       string email = GetTestEmail();

                       try
                       {
                           for (int attempt = 0; attempt < 4; attempt++)
                           {
                               for (bool requireEmail = true; requireEmail; requireEmail = !requireEmail)
                               {
                                   await newsletterService.SetSubscription(email, true, requireEmail);
                                   Assert.IsTrue(await newsletterService.IsSubscribedAsync(email));

                                   await newsletterService.SetSubscription(email, false, requireEmail);
                                   Assert.IsFalse(await newsletterService.IsSubscribedAsync(email));
                               }
                           }
                       }
                       finally
                       {
                           await newsletterService.SetSubscription(email, false, true);
                       }
                   });

        [TestMethod]
        public Task TestUnsubscribeNonExistingListMember()
            => WithServiceProvider(
                   ConfigureReplacementServices,
                   async scope =>
                   {
                       var newsletterService = scope.ServiceProvider.GetRequiredService<INewsletterService>();
                       string email = GetTestEmail();

                       await newsletterService.SetSubscription(email, false, false);
                       Assert.IsFalse(await newsletterService.IsSubscribedAsync(email));
                   });

        private void ConfigureReplacementServices(ServiceCollection sc)
        {
            var jobClient = new Mock<IBackgroundJobClient>();
            sc.AddScoped(s => jobClient.Object);
        }

        private string GetTestEmail()
            => $"collaction-test-email-{Guid.NewGuid()}@outlook.com";
    }
}