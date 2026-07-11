namespace ListaryOpen.Core.Search;

internal readonly record struct TextMatchKey(int Tier, double Quality, int LengthDifference)
{
    public static TextMatchKey NoMatch => new(int.MaxValue, 0, int.MaxValue);

    public bool IsMatch => Tier != int.MaxValue;
}
