namespace SimuLean.Collections
{
    /// <summary>
    /// A value that gives a <see cref="SimList"/> its own fields, read live whenever a query or an order needs
    /// them (e.g. a resource: idle time, utilisation, skills; a resource request: priority, station). Fields of
    /// the list (aliases, formulas) and the built-ins are resolved first; then this source; then the fields
    /// given at push time.
    /// </summary>
    public interface IListFieldSource
    {
        /// <summary>Numeric value of <paramref name="field"/>, or NaN if it has none (or it is text).</summary>
        double GetListNumber(string field);

        /// <summary>Text value of <paramref name="field"/>, or null if it has none.</summary>
        string GetListText(string field);
    }
}
