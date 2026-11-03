using System;
using System.Threading;
using System.Threading.Tasks;

namespace CentralRouting.Router
{
    static class StoreExtensions
    {
        const int MaxAttempts = 10;

        /// <summary>
        /// Loads an endpoint (or creates a new one), applies the change and saves it, retrying on concurrency conflicts.
        /// The change returns false when nothing needs to be saved.
        /// </summary>
        public static async Task<bool> UpdateEndpoint(this IRouterStore store, string name, bool createIfMissing,
            Func<EndpointRecord, bool> change, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                var endpoint = await store.GetEndpoint(name, cancellationToken).ConfigureAwait(false);
                if (endpoint == null)
                {
                    if (!createIfMissing)
                    {
                        return false;
                    }
                    endpoint = new EndpointRecord { Name = name };
                }

                if (!change(endpoint))
                {
                    return false;
                }

                try
                {
                    await store.SaveEndpoint(endpoint, cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (StoreConcurrencyException) when (attempt < MaxAttempts)
                {
                    //Another report or API call changed the endpoint. Load it again and re-apply.
                }
            }
        }
    }
}
