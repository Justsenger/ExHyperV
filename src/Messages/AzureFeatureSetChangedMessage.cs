using CommunityToolkit.Mvvm.Messaging.Messages;

namespace ExHyperV.Messages;

public sealed class AzureFeatureSetChangedMessage(bool enabled)
    : ValueChangedMessage<bool>(enabled);
