﻿using CollAction.Data;
using CollAction.Helpers;
using CollAction.Models;
using CollAction.Services.Donation.Models;
using CollAction.Services.Email;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace CollAction.Services.Donation
{
    /// <summary>
    /// There are 4 donation flows:
    /// * Non-recurring iDeal payments
    ///   - The flow is started at DebitDetails.tsx where a source with/the relevant details is created client-side
    ///   - This source is sent to graphql InitializeIdealCheckout, where this source is attached to a customer-id (this can't be done with the public stripe API keys)
    ///   - After that, the DebitDetails component will redirect the user to the source redirect-url, where the user will get his bank iDeal dialog
    ///   - On success, the user will be redirected to the return page, which will redirect the user to the thank-you page if successfull, otherwise the user will be redirected to the donation-page
    ///   - Stripe will POST to the chargeable webhook (set to /Donation/Chargeable if the stripe settings are correct). This will finish the iDeal payment. If these settings aren't correct, we won't receive the payment.
    /// * Non-recurring credit card payments
    ///   - All the details are gathered in DonationCard.tsx, and are sent to graphql InitializeCreditCardCheckout
    ///   - Here we'll initiate a "Checkout" session for the credit card payment
    ///   - From graphql InitializeCreditCardCheckout, we return the checkout-id. In DonationCard.tsx we use stripe.js to redirect user to the checkout page
    ///   - If successfull, the user will be returned to the thank-you page, otherwise the user will be redirected to the donation-page
    ///   - Checkout will auto-charge, so the webhook won't be necessary
    /// * Recurring SEPA Direct payments
    ///   - The flow is started at DebitDetails.tsx where a source with the relevant details is created client-side
    ///   - This source is sent to graphql InitializeSepaDirect, where this source is attached to a auto-charged recurring subscription
    ///   - On success, the user will be redirected to the thank-you page, otherwise the user will be shown an error
    ///   - Checkout/Billing will auto-charge the subscription, so the webhook won't be necessary
    /// * Recurring credit card payments
    ///   - All the details are gathered in DonationCard.tsx, and are sent to graphql InitializeCreditCardCheckout
    ///   - Here we'll initiate a "Checkout" session with a monthly subscription + plan for the credit card payment
    ///   - From graphql InitializeCreditCardCheckout, we return the checkout-id. In DonationCard.tsx we use stripe.js to redirect user to the checkout page
    ///   - If successfull, the user will be returned to the thank-you page, otherwise the user will be redirected to the donation-page
    ///   - Checkout/Billing will auto-charge the subscription, so the webhook won't be necessary
    /// </summary>
    public sealed class DonationService : IDonationService
    {
        private const string StatusChargeable = "chargeable";
        private const string StatusConsumed = "consumed";
        private const string EventTypeChargeableSource = "source.chargeable";
        private const string EventTypeChargeSucceeded = "charge.succeeded";
        private const string NameKey = "name";
        private const string RecurringDonationProduct = "Recurring Donation Stichting CollAction";

        private readonly CustomerService customerService;
        private readonly SourceService sourceService;
        private readonly ChargeService chargeService;
        private readonly SessionService sessionService;
        private readonly SubscriptionService subscriptionService;
        private readonly PlanService planService;
        private readonly ProductService productService;

        private readonly IBackgroundJobClient backgroundJobClient;
        private readonly IEmailSender emailSender;
        private readonly ILogger<DonationService> logger;
        private readonly IServiceProvider serviceProvider;
        private readonly StripeSignatures stripeSignatures;
        private readonly ApplicationDbContext context;
        private readonly UserManager<ApplicationUser> userManager;
        private readonly RequestOptions requestOptions;

        public DonationService(
            IOptions<RequestOptions> requestOptions,
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context,
            IBackgroundJobClient backgroundJobClient,
            IOptions<StripeSignatures> stripeSignatures,
            IEmailSender emailSender,
            IServiceProvider serviceProvider,
            ILogger<DonationService> logger)
        {
            this.requestOptions = requestOptions.Value;
            this.backgroundJobClient = backgroundJobClient;
            this.stripeSignatures = stripeSignatures.Value;
            this.context = context;
            this.userManager = userManager;
            this.emailSender = emailSender;
            this.logger = logger;
            this.serviceProvider = serviceProvider;
            customerService = new CustomerService(this.requestOptions.ApiKey);
            sourceService = new SourceService(this.requestOptions.ApiKey);
            chargeService = new ChargeService(this.requestOptions.ApiKey);
            sessionService = new SessionService(this.requestOptions.ApiKey);
            subscriptionService = new SubscriptionService(this.requestOptions.ApiKey);
            planService = new PlanService(this.requestOptions.ApiKey);
            productService = new ProductService(this.requestOptions.ApiKey);
        }

        public async Task<bool> HasIDealPaymentSucceeded(string sourceId, string clientSecret, CancellationToken token)
        {
            Source source = await sourceService.GetAsync(sourceId, cancellationToken: token).ConfigureAwait(false);
            return (source.Status == StatusChargeable || source.Status == StatusConsumed) &&
                   clientSecret == source.ClientSecret;
        }

        /*
         * Here we're initializing a stripe checkout session for paying with a credit card. The upcoming SCA regulations ensure we have to do it through this API, because it's the only one that /easily/ supports things like 3D secure.
         */
        public async Task<string> InitializeCreditCardCheckout(CreditCardCheckout checkout, CancellationToken token)
        {
            IEnumerable<ValidationResult> validationResults = ValidationHelper.Validate(checkout, serviceProvider);
            if (validationResults.Any())
            {
                throw new InvalidOperationException(string.Join(",", validationResults.Select(v => $"{string.Join(", ", v.MemberNames)}: {v.ErrorMessage}")));
            }

            ApplicationUser user = await userManager.FindByEmailAsync(checkout.Email).ConfigureAwait(false);

            var sessionOptions = new SessionCreateOptions()
            {
                SuccessUrl = checkout.SuccessUrl,
                CancelUrl = checkout.CancelUrl,
                PaymentMethodTypes = new List<string>
                {
                    "card",
                }
            };

            if (checkout.Recurring)
            {
                logger.LogInformation("Initializing recurring credit card checkout session");
                sessionOptions.CustomerEmail = checkout.Email; // TODO: sessionOptions.CustomerId = customer.Id; // Once supported
                sessionOptions.SubscriptionData = new SessionSubscriptionDataOptions()
                {
                    Items = new List<SessionSubscriptionDataItemOptions>()
                    {
                        new SessionSubscriptionDataItemOptions()
                        {
                            PlanId = (await CreateRecurringPlan(checkout.Amount, checkout.Currency, token).ConfigureAwait(false)).Id,
                            Quantity = 1
                        }
                    }
                };
            }
            else
            {
                logger.LogInformation("Initializing credit card checkout session");
                Customer customer = await GetOrCreateCustomer(checkout.Name, checkout.Email, token).ConfigureAwait(false);
                sessionOptions.CustomerId = customer.Id;
                sessionOptions.LineItems = new List<SessionLineItemOptions>()
                {
                    new SessionLineItemOptions()
                    {
                        Amount = checkout.Amount * 100,
                        Currency = checkout.Currency,
                        Name = "donation",
                        Description = "A donation to Stichting CollAction",
                        Quantity = 1
                    }
                };
            }

            Session session = await sessionService.CreateAsync(sessionOptions, cancellationToken: token).ConfigureAwait(false);

            context.DonationEventLog.Add(new DonationEventLog(eventData: session.ToJson(), type: DonationEventType.Internal, userId: user?.Id));
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            logger.LogInformation("Done initializing credit card checkout session");

            return session.Id;
        }

        /*
         * Here we're initializing a stripe SEPA subscription on a source with sepa/iban data. This subscription should auto-charge.
         */
        public async Task InitializeSepaDirect(SepaDirectCheckout checkout, CancellationToken token)
        {
            logger.LogInformation("Initializing sepa direct");
            IEnumerable<ValidationResult> validationResults = ValidationHelper.Validate(checkout, serviceProvider);
            if (validationResults.Any())
            {
                throw new InvalidOperationException(string.Join(",", validationResults.Select(v => $"{string.Join(", ", v.MemberNames)}: {v.ErrorMessage}")));
            }

            ApplicationUser user = await userManager.FindByEmailAsync(checkout.Email).ConfigureAwait(false);
            Customer customer = await GetOrCreateCustomer(checkout.Name, checkout.Email, token).ConfigureAwait(false);
            Source source = await sourceService.AttachAsync(
                customer.Id,
                new SourceAttachOptions()
                {
                    Source = checkout.SourceId
                },
                cancellationToken: token).ConfigureAwait(false);
            Plan plan = await CreateRecurringPlan(checkout.Amount, "eur", token).ConfigureAwait(false);
            Subscription subscription = await subscriptionService.CreateAsync(
                new SubscriptionCreateOptions()
                {
                    DefaultSource = source.Id,
                    Billing = Billing.ChargeAutomatically,
                    CustomerId = customer.Id,
                    Items = new List<SubscriptionItemOption>()
                    {
                        new SubscriptionItemOption()
                        {
                            PlanId = plan.Id,
                            Quantity = 1
                        }
                    }
                },
                cancellationToken: token).ConfigureAwait(false);

            context.DonationEventLog.Add(new DonationEventLog(userId: user?.Id, type: DonationEventType.Internal, eventData: subscription.ToJson()));
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            logger.LogInformation("Done initializing sepa direct");
        }

        /*
         * Here we're initializing the part of the iDeal payment that has to happen on the backend. For now, that's only attaching an actual customer record to the payment source.
         * In the future to handle SCA, we might need to start using payment intents or checkout here. SCA starts from september the 14th. The support for iDeal is not there yet though, so we'll have to wait.
         */
        public async Task InitializeIDealCheckout(IDealCheckout checkout, CancellationToken token)
        {
            logger.LogInformation("Initializing iDeal");
            IEnumerable<ValidationResult> validationResults = ValidationHelper.Validate(checkout, serviceProvider);
            if (validationResults.Any())
            {
                throw new InvalidOperationException(string.Join(",", validationResults.Select(v => $"{string.Join(", ", v.MemberNames)}: {v.ErrorMessage}")));
            }

            ApplicationUser user = await userManager.FindByEmailAsync(checkout.Email).ConfigureAwait(false);
            Customer customer = await GetOrCreateCustomer(checkout.Name, checkout.Email, token).ConfigureAwait(false);
            Source source = await sourceService.AttachAsync(
                customer.Id,
                new SourceAttachOptions()
                {
                    Source = checkout.SourceId
                },
                cancellationToken: token).ConfigureAwait(false);

            context.DonationEventLog.Add(new DonationEventLog(userId: user?.Id, type: DonationEventType.Internal, eventData: source.ToJson()));
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            logger.LogInformation("Done initializing iDeal");
        }

        /*
         * We're receiving an event from the stripe webhook, an payment source can be charge. We're queueing it up so we can retry it as much as possible.
         * In the future to handle SCA, we might need to start using payment intents or checkout here. SCA starts from september the 14th. The support for iDeal is not there yet though, so we'll have to wait.
         */
        public void HandleChargeable(string json, string signature)
        {
            logger.LogInformation("Received chargeable");
            Event stripeEvent = EventUtility.ConstructEvent(json, signature, stripeSignatures.StripeChargeableWebhookSecret);
            if (stripeEvent.Type == EventTypeChargeableSource)
            {
                Source source = (Source)stripeEvent.Data.Object;
                backgroundJobClient.Enqueue(() => Charge(source.Id));
            }
            else
            {
                throw new InvalidOperationException($"invalid event sent to source.chargeable webhook: {stripeEvent.ToJson()}");
            }
        }

        /*
         * We're logging all stripe events here. For audit purposes, and maybe the dwh team can make something pretty out of this data.
         */
        public async Task LogPaymentEvent(string json, string signature, CancellationToken token)
        {
            logger.LogInformation("Received payment event");
            Event stripeEvent = EventUtility.ConstructEvent(json, signature, stripeSignatures.StripePaymentEventWebhookSecret);

            context.DonationEventLog.Add(new DonationEventLog(type: DonationEventType.External, eventData: stripeEvent.ToJson()));
            await context.SaveChangesAsync(token).ConfigureAwait(false);

            if (stripeEvent.Type == EventTypeChargeSucceeded)
            {
                Charge charge = (Charge)stripeEvent.Data.Object;
                var subscriptions = await subscriptionService.ListAsync(
                    new SubscriptionListOptions()
                    {
                        CustomerId = charge.CustomerId
                    },
                    cancellationToken: token).ConfigureAwait(false);
                Customer customer = await customerService.GetAsync(charge.CustomerId, cancellationToken: token).ConfigureAwait(false);
                await SendDonationThankYou(customer, subscriptions.Any()).ConfigureAwait(false);
            }
        }

        /*
         * Charge a source here (only used for iDeal right now). It's a background job so it can be restarted.
         */
        public async Task Charge(string sourceId)
        {
            logger.LogInformation("Processing chargeable");
            Source source = await sourceService.GetAsync(sourceId).ConfigureAwait(false);
            if (source.Status == StatusChargeable)
            {
                Charge charge;
                try
                {
                    charge = await chargeService.CreateAsync(new ChargeCreateOptions()
                    {
                        Amount = source.Amount,
                        Currency = source.Currency,
                        SourceId = sourceId,
                        CustomerId = source.Customer,
                        Description = "A donation to Stichting CollAction"
                    }).ConfigureAwait(false);
                }
                catch (StripeException e)
                {
                    logger.LogError(e, "Error processing chargeable");
                    throw;
                }

                Customer customer = await customerService.GetAsync(source.Customer).ConfigureAwait(false);
                ApplicationUser? user = customer != null ? await userManager.FindByEmailAsync(customer.Email).ConfigureAwait(false) : null;
                context.DonationEventLog.Add(new DonationEventLog(userId: user?.Id, type: DonationEventType.Internal, eventData: charge.ToJson()));
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
            else
            {
                logger.LogError("Invalid chargeable received");
                throw new InvalidOperationException($"source: {source.Id} is not chargeable, something went wrong in the payment flow");
            }
        }

        public async Task<IEnumerable<Subscription>> GetSubscriptionsFor(ApplicationUser userFor, CancellationToken token)
        {
            var customers = await customerService.ListAsync(
                new CustomerListOptions()
                {
                    Email = userFor.Email
                },
                cancellationToken: token).ConfigureAwait(false);

            var subscriptions = await Task.WhenAll(
                customers.Select(c =>
                    subscriptionService.ListAsync(
                        new SubscriptionListOptions()
                        {
                            CustomerId = c.Id
                        },
                        cancellationToken: token))).ConfigureAwait(false);

            return subscriptions.SelectMany(s => s);
        }

        public async Task CancelSubscription(string subscriptionId, ClaimsPrincipal userFor, CancellationToken token)
        {
            var user = await userManager.GetUserAsync(userFor).ConfigureAwait(false);
            Subscription subscription = await subscriptionService.GetAsync(subscriptionId, cancellationToken: token).ConfigureAwait(false);
            Customer customer = await customerService.GetAsync(subscription.CustomerId, cancellationToken: token).ConfigureAwait(false);

            if (!customer.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"User {user.Email} doesn't match subscription e-mail {subscription.Customer.Email}");
            }

            subscription = await subscriptionService.CancelAsync(subscriptionId, new SubscriptionCancelOptions(), cancellationToken: token).ConfigureAwait(false);

            context.DonationEventLog.Add(new DonationEventLog(userId: user.Id, type: DonationEventType.Internal, eventData: subscription.ToJson()));
            await context.SaveChangesAsync(token).ConfigureAwait(false);
        }

        private async Task<Plan> CreateRecurringPlan(int amount, string currency, CancellationToken token)
        {
            Product product = await GetOrCreateRecurringDonationProduct(token).ConfigureAwait(false);
            return await planService.CreateAsync(
                new PlanCreateOptions()
                {
                    ProductId = product.Id,
                    Active = true,
                    Amount = amount * 100,
                    Currency = currency,
                    Interval = "month",
                    BillingScheme = "per_unit",
                    UsageType = "licensed",
                    IntervalCount = 1
                },
                cancellationToken: token).ConfigureAwait(false);
        }

        private async Task<Product> GetOrCreateRecurringDonationProduct(CancellationToken token)
        {
            var products = await productService.ListAsync(
                new ProductListOptions()
                {
                    Active = true,
                    Type = "service"
                },
                cancellationToken: token).ConfigureAwait(false);
            Product? product = products.FirstOrDefault(c => c.Name == RecurringDonationProduct);
            if (product == null)
            {
                product = await productService.CreateAsync(
                    new ProductCreateOptions()
                    {
                        Active = true,
                        Name = RecurringDonationProduct,
                        StatementDescriptor = "Donation CollAction",
                        Type = "service"
                    },
                    cancellationToken: token).ConfigureAwait(false);
            }

            return product;
        }

        private async Task<Customer> GetOrCreateCustomer(string name, string email, CancellationToken token)
        {
            Customer? customer = (await customerService.ListAsync(new CustomerListOptions() { Email = email, Limit = 1 }, requestOptions, token).ConfigureAwait(false)).FirstOrDefault();
            string? metadataName = null;
            customer?.Metadata?.TryGetValue(NameKey, out metadataName);
            var metadata = new Dictionary<string, string>() { { NameKey, name } };
            if (customer == null)
            {
                customer = await customerService.CreateAsync(
                    new CustomerCreateOptions()
                    {
                        Email = email,
                        Metadata = metadata
                    },
                    cancellationToken: token).ConfigureAwait(false);
            }
            else if (!name.Equals(metadataName, StringComparison.Ordinal))
            {
                customer = await customerService.UpdateAsync(
                    customer.Id,
                    new CustomerUpdateOptions()
                    {
                        Metadata = metadata
                    },
                    cancellationToken: token).ConfigureAwait(false);
            }

            return customer;
        }

        private Task SendDonationThankYou(Customer customer, bool hasSubscriptions)
            => emailSender.SendEmailTemplated(customer.Email, "Thank you for your donation", "DonationThankYou", hasSubscriptions);
    }
}