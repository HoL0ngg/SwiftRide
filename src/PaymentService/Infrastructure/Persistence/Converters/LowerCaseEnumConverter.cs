using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
namespace SwiftRide.PaymentService.Infrastructure.Persistence.Converters;

// DB (Supabase) lưu enum dạng chữ thường: 'card', 'settled', 'debit'... (có CHECK ép đúng giá trị)
// C# dùng PascalCase (Card, Settled, Debit) => chuyển qua lại ở đây.
public sealed class LowerCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => v.ToString().ToLowerInvariant(),
    v => Enum.Parse<TEnum>(v, true)) where TEnum : struct, Enum;
