// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace idunno.Bluesky.BotSdk;

/// <summary>Indicates that a storage operation cannot proceed because this process does not hold the ownership lease.</summary>
/// <remarks><para>Database providers throw this exception when fencing rejects an operation. Workers stop admitting work and wait for ownership recovery.</para></remarks>
public sealed class OwnershipLostException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="OwnershipLostException"/> class.</summary>
    public OwnershipLostException() : base("Database ownership is not held.")
    {
    }
}