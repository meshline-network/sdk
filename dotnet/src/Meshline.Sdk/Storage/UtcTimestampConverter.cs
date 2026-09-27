using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Meshline.Storage;

sealed class UtcTimestampConverter() : ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
