namespace Quiche;

internal readonly struct size_t 
{
    private readonly uint value;

    private size_t(uint value) 
    {
        this.value = value;
    }

    private size_t(int value) 
    {
        this.value = unchecked((uint)value);
    }

    public static explicit operator size_t(uint value) => new size_t(value);

    public static explicit operator size_t(int value) => new size_t(value);

    public static implicit operator uint(size_t size) => size.value;

    public static implicit operator int(size_t size) => unchecked((int)size.value);
}
