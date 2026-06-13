using WebApiPlayground.Application.DTOs;
using Xunit;

namespace WebApiPlayground.Tests.Querying;

/// <summary>
/// La matematica dei metadati di paginazione ai BORDI: prima/ultima pagina, divisione esatta vs
/// arrotondata in alto, set vuoto e il caso degenere PageSize=0 (mai una divisione per zero).
/// Sono i valori che i client usano per costruire la navigazione: un off-by-one qui è un bug visibile.
/// </summary>
public class PagedResultTests
{
    private static PagedResult<int> Page(int pageNumber, int pageSize, int totalCount) =>
        new([], pageNumber, pageSize, totalCount);

    [Theory]
    [InlineData(10, 3, 4)]  // 10/3 → arrotonda IN ALTO: l'ultima pagina parziale esiste
    [InlineData(10, 5, 2)]  // divisione esatta
    [InlineData(1, 10, 1)]  // meno item di una pagina
    [InlineData(0, 10, 0)]  // set vuoto → zero pagine
    public void Total_pages_is_the_ceiling_of_count_over_size(int totalCount, int pageSize, int expected)
    {
        Assert.Equal(expected, Page(1, pageSize, totalCount).TotalPages);
    }

    [Fact]
    public void Page_size_zero_degenerates_to_zero_pages_not_a_crash()
    {
        Assert.Equal(0, Page(1, 0, 10).TotalPages);
    }

    [Theory]
    [InlineData(1, false, true)]   // prima pagina: niente indietro, avanti sì
    [InlineData(2, true, true)]    // pagina centrale: entrambe
    [InlineData(3, true, false)]   // ultima pagina: niente avanti
    public void Has_previous_and_next_track_the_position_in_the_set(int pageNumber, bool hasPrevious, bool hasNext)
    {
        var page = Page(pageNumber, 10, 30); // 3 pagine totali

        Assert.Equal(hasPrevious, page.HasPrevious);
        Assert.Equal(hasNext, page.HasNext);
    }

    [Fact]
    public void Single_page_has_neither_previous_nor_next()
    {
        var page = Page(1, 10, 5);

        Assert.False(page.HasPrevious);
        Assert.False(page.HasNext);
    }
}
