﻿using CollAction.Services.Donation;
using GraphQL.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CollAction.GraphQl.Queries
{
    public sealed class DonationGraph : ObjectGraphType
    {
        public DonationGraph(IServiceScopeFactory serviceScopeFactory, IOptions<StripePublicOptions> stripePublicOptions)
        {
            FieldAsync<BooleanGraphType>(
                "hasIDealPaymentSucceeded",
                arguments: new QueryArguments(
                    new QueryArgument<NonNullGraphType<StringGraphType>>() { Name = "source" },
                    new QueryArgument<NonNullGraphType<StringGraphType>>() { Name = "clientSecret" }),
                resolve: async c =>
                {
                    string source = c.GetArgument<string>("source");
                    string clientSecret = c.GetArgument<string>("clientSecret");
                    using var scope = serviceScopeFactory.CreateScope();
                    return await scope.ServiceProvider.GetRequiredService<IDonationService>().HasIDealPaymentSucceeded(source, clientSecret, c.CancellationToken).ConfigureAwait(false);
                });

            Field<NonNullGraphType<StringGraphType>>(
                "stripePublicKey",
                resolve: c => stripePublicOptions.Value.StripePublicKey);
        }
    }
}
