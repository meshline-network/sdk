namespace Meshline.Models;

/// <summary>
/// Represents an unchanged field, an assigned value, or an explicit field deletion.
/// </summary>
/// <typeparam name="T">The non-null type of the field value.</typeparam>
/// <remarks>
/// The default value means no change. <see cref="Delete"/> sets <see cref="IsDeleted"/> without setting <see cref="IsSpecified"/>. Protocol serialization omits unchanged fields, writes deletions as JSON null, and writes assigned values normally. Individual operations may prohibit deleting required fields.
/// </remarks>
public readonly struct FieldUpdate<T> where T : notnull
{
    /// <summary>
    /// An update that leaves the existing field unchanged.
    /// </summary>
    public static FieldUpdate<T> Unspecified => default;
    /// <summary>
    /// An update that explicitly deletes the field instead of assigning a value.
    /// </summary>
    public static FieldUpdate<T> Delete => new(true);

    /// <summary>
    /// Whether this update assigns a value; deletion is represented separately by <see cref="IsDeleted"/>.
    /// </summary>
    public bool IsSpecified { get; }
    /// <summary>
    /// Whether this update explicitly deletes the field.
    /// </summary>
    public bool IsDeleted { get; }

    /// <summary>
    /// The assigned value; accessing an unchanged or deleted field throws <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field update does not assign a value.</exception>
    public T Value => IsSpecified && !IsDeleted ? field : throw new InvalidOperationException("No assigned value.");

    /// <summary>
    /// Initializes a new instance of <see cref="FieldUpdate{T}"/>.
    /// </summary>
    /// <param name="value">The value to assign to the field.</param>
    public FieldUpdate(T value)
    {
        Value = value;
        IsSpecified = true;
        IsDeleted = false;
    }

    private FieldUpdate(bool delete)
    {
        Value = default!;
        IsSpecified = false;
        IsDeleted = delete;
    }

    /// <summary>
    /// Creates a field assignment from a value.
    /// </summary>
    /// <param name="value">The value to assign to the field.</param>
    /// <returns>An update assigning the supplied value.</returns>
    public static implicit operator FieldUpdate<T>(T value) => new(value);
}
