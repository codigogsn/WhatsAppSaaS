namespace WhatsAppSaaS.Application.Services;

public sealed class RestaurantTemplate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<TemplateCategory> DefaultCategories { get; init; } = new();
    public Dictionary<string, string[]> SuggestedUpsells { get; init; } = new();
}

public sealed class TemplateCategory
{
    public string Name { get; init; } = "";
    public List<TemplateItem> Items { get; init; } = new();
}

public sealed class TemplateItem
{
    public string Name { get; init; } = "";
    public decimal Price { get; init; }
    public string[] Aliases { get; init; } = [];
}

public static class RestaurantTemplates
{
    public static readonly IReadOnlyDictionary<string, RestaurantTemplate> All = new Dictionary<string, RestaurantTemplate>(StringComparer.OrdinalIgnoreCase)
    {
        ["burger"] = new RestaurantTemplate
        {
            Id = "burger",
            Name = "Hamburguesas",
            Description = "Restaurante de hamburguesas, perros calientes, papas y bebidas",
            DefaultCategories =
            [
                new TemplateCategory
                {
                    Name = "Hamburguesas",
                    Items =
                    [
                        new TemplateItem { Name = "Hamburguesa Clasica", Price = 6.50m, Aliases = ["clasica", "hamburguesa", "burger", "burguer", "hamburguesa clasica"] },
                        new TemplateItem { Name = "Hamburguesa Doble", Price = 8.50m, Aliases = ["doble", "double", "hamburguesa doble"] },
                        new TemplateItem { Name = "Hamburguesa Bacon", Price = 9.00m, Aliases = ["bacon", "hamburguesa bacon", "hamburguesa con bacon", "con tocineta"] },
                        new TemplateItem { Name = "Hamburguesa Especial", Price = 10.50m, Aliases = ["especial", "hamburguesa especial", "la especial"] },
                        new TemplateItem { Name = "Hamburguesa BBQ", Price = 9.50m, Aliases = ["bbq", "hamburguesa bbq", "barbacoa"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Perros Calientes",
                    Items =
                    [
                        new TemplateItem { Name = "Perro Clasico", Price = 4.50m, Aliases = ["perro", "perro caliente", "hot dog", "hotdog", "perro clasico"] },
                        new TemplateItem { Name = "Perro Especial", Price = 6.00m, Aliases = ["perro especial"] },
                        new TemplateItem { Name = "Perro con Queso", Price = 5.50m, Aliases = ["perro con queso", "perro queso"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Papas",
                    Items =
                    [
                        new TemplateItem { Name = "Papas Pequenas", Price = 2.50m, Aliases = ["papas pequenas", "papas pequena", "papitas", "papas chicas"] },
                        new TemplateItem { Name = "Papas Medianas", Price = 3.50m, Aliases = ["papas medianas", "papas mediana", "papas"] },
                        new TemplateItem { Name = "Papas Grandes", Price = 4.50m, Aliases = ["papas grandes", "papas grande"] },
                        new TemplateItem { Name = "Papas con Queso", Price = 5.50m, Aliases = ["papas con queso", "papas queso"] },
                        new TemplateItem { Name = "Papas Mixtas", Price = 6.50m, Aliases = ["papas mixtas", "papas mixta", "mixtas"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Bebidas",
                    Items =
                    [
                        new TemplateItem { Name = "Coca Cola 355ml", Price = 1.50m, Aliases = ["coca", "coca cola", "cocacola", "refresco", "coca cola 355", "coca pequena"] },
                        new TemplateItem { Name = "Coca Cola 1L", Price = 2.50m, Aliases = ["coca grande", "coca cola grande", "coca litro", "coca cola 1l"] },
                        new TemplateItem { Name = "Pepsi 355ml", Price = 1.50m, Aliases = ["pepsi", "pepsi 355"] },
                        new TemplateItem { Name = "Te Frio", Price = 1.75m, Aliases = ["te frio", "te", "te helado", "iced tea"] },
                        new TemplateItem { Name = "Agua", Price = 1.00m, Aliases = ["agua", "water", "botella de agua"] },
                        new TemplateItem { Name = "Malta", Price = 1.50m, Aliases = ["malta", "maltita", "maltin"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Combos",
                    Items =
                    [
                        new TemplateItem { Name = "Combo Clasico", Price = 8.50m, Aliases = ["combo clasico", "combo 1", "combo"] },
                        new TemplateItem { Name = "Combo Doble", Price = 10.50m, Aliases = ["combo doble", "combo 2"] },
                        new TemplateItem { Name = "Combo Bacon", Price = 11.00m, Aliases = ["combo bacon", "combo 3"] },
                        new TemplateItem { Name = "Combo Perro", Price = 6.50m, Aliases = ["combo perro", "combo hot dog"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Extras",
                    Items =
                    [
                        new TemplateItem { Name = "Extra Queso", Price = 1.00m, Aliases = ["extra queso", "queso extra", "mas queso"] },
                        new TemplateItem { Name = "Extra Tocineta", Price = 1.50m, Aliases = ["extra tocineta", "tocineta extra", "extra bacon", "mas tocineta"] },
                        new TemplateItem { Name = "Extra Carne", Price = 2.50m, Aliases = ["extra carne", "carne extra", "doble carne", "mas carne"] },
                        new TemplateItem { Name = "Extra Huevo", Price = 1.00m, Aliases = ["extra huevo", "huevo extra", "con huevo", "mas huevo"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Salsas",
                    Items =
                    [
                        new TemplateItem { Name = "Salsa Ajo", Price = 0.50m, Aliases = ["salsa ajo", "salsa de ajo", "ajo"] },
                        new TemplateItem { Name = "Salsa Tartara", Price = 0.50m, Aliases = ["salsa tartara", "tartara"] },
                        new TemplateItem { Name = "Salsa Picante", Price = 0.50m, Aliases = ["salsa picante", "picante"] },
                        new TemplateItem { Name = "Salsa Rosada", Price = 0.50m, Aliases = ["salsa rosada", "rosada", "salsa rosa"] },
                    ]
                }
            ],
            SuggestedUpsells = new()
            {
                ["hamburguesas"] = ["bebidas", "papas"],
                ["perros calientes"] = ["bebidas", "papas"],
                ["bebidas"] = ["hamburguesas", "papas"],
                ["papas"] = ["bebidas"],
                ["extras"] = ["bebidas"],
                ["salsas"] = ["bebidas"],
            }
        },

        ["pizza"] = new RestaurantTemplate
        {
            Id = "pizza",
            Name = "Pizzeria",
            Description = "Pizzeria con pastas y bebidas",
            DefaultCategories =
            [
                new TemplateCategory
                {
                    Name = "Pizzas",
                    Items =
                    [
                        new TemplateItem { Name = "Pizza Margarita", Price = 8.00m, Aliases = ["margarita", "margherita"] },
                        new TemplateItem { Name = "Pizza Pepperoni", Price = 9.00m, Aliases = ["pepperoni"] },
                        new TemplateItem { Name = "Pizza Hawaiana", Price = 9.00m, Aliases = ["hawaiana", "hawaiian"] },
                        new TemplateItem { Name = "Pizza Vegetariana", Price = 8.50m, Aliases = ["vegetariana", "vegetal"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Pastas",
                    Items =
                    [
                        new TemplateItem { Name = "Spaghetti Bolognesa", Price = 6.00m, Aliases = ["spaghetti", "bolognesa", "espagueti"] },
                        new TemplateItem { Name = "Lasagna", Price = 7.00m, Aliases = ["lasagna", "lasana"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Bebidas",
                    Items =
                    [
                        new TemplateItem { Name = "Coca Cola", Price = 1.50m, Aliases = ["coca", "cola"] },
                        new TemplateItem { Name = "Agua", Price = 1.00m, Aliases = ["agua"] },
                        new TemplateItem { Name = "Cerveza", Price = 2.50m, Aliases = ["cerveza", "birra", "beer"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Extras",
                    Items =
                    [
                        new TemplateItem { Name = "Pan de Ajo", Price = 2.00m, Aliases = ["pan de ajo", "garlic bread"] },
                        new TemplateItem { Name = "Ensalada Cesar", Price = 3.50m, Aliases = ["ensalada", "cesar", "caesar"] },
                    ]
                }
            ],
            SuggestedUpsells = new()
            {
                ["pizzas"] = ["bebidas", "extras"],
                ["pastas"] = ["bebidas", "extras"],
                ["bebidas"] = ["pizzas", "extras"],
            }
        },

        ["sushi"] = new RestaurantTemplate
        {
            Id = "sushi",
            Name = "Sushi",
            Description = "Restaurante japones con sushi, rolls y bebidas",
            DefaultCategories =
            [
                new TemplateCategory
                {
                    Name = "Rolls",
                    Items =
                    [
                        new TemplateItem { Name = "California Roll", Price = 7.00m, Aliases = ["california", "california roll"] },
                        new TemplateItem { Name = "Philadelphia Roll", Price = 7.50m, Aliases = ["philadelphia", "philly"] },
                        new TemplateItem { Name = "Tempura Roll", Price = 8.00m, Aliases = ["tempura roll", "tempura"] },
                        new TemplateItem { Name = "Dragon Roll", Price = 9.00m, Aliases = ["dragon", "dragon roll"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Nigiri",
                    Items =
                    [
                        new TemplateItem { Name = "Nigiri Salmon", Price = 4.00m, Aliases = ["nigiri salmon", "salmon nigiri"] },
                        new TemplateItem { Name = "Nigiri Atun", Price = 4.50m, Aliases = ["nigiri atun", "atun nigiri", "tuna nigiri"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Bebidas",
                    Items =
                    [
                        new TemplateItem { Name = "Te Verde", Price = 2.00m, Aliases = ["te verde", "green tea", "te"] },
                        new TemplateItem { Name = "Sake", Price = 4.00m, Aliases = ["sake"] },
                        new TemplateItem { Name = "Agua", Price = 1.00m, Aliases = ["agua"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Extras",
                    Items =
                    [
                        new TemplateItem { Name = "Edamame", Price = 3.00m, Aliases = ["edamame"] },
                        new TemplateItem { Name = "Gyozas", Price = 4.00m, Aliases = ["gyozas", "dumplings", "gyoza"] },
                        new TemplateItem { Name = "Sopa Miso", Price = 2.50m, Aliases = ["miso", "sopa miso", "sopa"] },
                    ]
                }
            ],
            SuggestedUpsells = new()
            {
                ["rolls"] = ["bebidas", "extras"],
                ["nigiri"] = ["bebidas", "extras"],
                ["bebidas"] = ["rolls", "extras"],
            }
        },

        ["arepa"] = new RestaurantTemplate
        {
            Id = "arepa",
            Name = "Arepera",
            Description = "Arepera venezolana con arepas, empanadas y jugos",
            DefaultCategories =
            [
                new TemplateCategory
                {
                    Name = "Arepas",
                    Items =
                    [
                        new TemplateItem { Name = "Arepa Reina Pepiada", Price = 3.50m, Aliases = ["reina pepiada", "pepiada"] },
                        new TemplateItem { Name = "Arepa Pabellon", Price = 4.00m, Aliases = ["pabellon", "pabellón"] },
                        new TemplateItem { Name = "Arepa Dominó", Price = 3.00m, Aliases = ["domino", "dominó"] },
                        new TemplateItem { Name = "Arepa Pelua", Price = 4.00m, Aliases = ["pelua", "pelúa"] },
                        new TemplateItem { Name = "Arepa Catira", Price = 3.50m, Aliases = ["catira"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Empanadas",
                    Items =
                    [
                        new TemplateItem { Name = "Empanada de Queso", Price = 2.00m, Aliases = ["empanada queso", "empanada de queso"] },
                        new TemplateItem { Name = "Empanada de Carne", Price = 2.50m, Aliases = ["empanada carne", "empanada de carne"] },
                        new TemplateItem { Name = "Empanada de Pollo", Price = 2.50m, Aliases = ["empanada pollo", "empanada de pollo"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Bebidas",
                    Items =
                    [
                        new TemplateItem { Name = "Jugo de Naranja", Price = 2.00m, Aliases = ["jugo naranja", "naranja", "jugo de naranja"] },
                        new TemplateItem { Name = "Jugo de Guayaba", Price = 2.00m, Aliases = ["jugo guayaba", "guayaba"] },
                        new TemplateItem { Name = "Malta", Price = 1.50m, Aliases = ["malta"] },
                        new TemplateItem { Name = "Coca Cola", Price = 1.50m, Aliases = ["coca", "cola"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Extras",
                    Items =
                    [
                        new TemplateItem { Name = "Tequeños", Price = 3.00m, Aliases = ["tequenos", "tequeños"] },
                        new TemplateItem { Name = "Cachapa con Queso", Price = 4.00m, Aliases = ["cachapa", "cachapa con queso"] },
                    ]
                }
            ],
            SuggestedUpsells = new()
            {
                ["arepas"] = ["bebidas", "extras"],
                ["empanadas"] = ["bebidas", "extras"],
                ["bebidas"] = ["arepas", "empanadas"],
            }
        },

        ["cafe"] = new RestaurantTemplate
        {
            Id = "cafe",
            Name = "Cafeteria",
            Description = "Cafeteria con bebidas calientes, postres y snacks",
            DefaultCategories =
            [
                new TemplateCategory
                {
                    Name = "Cafes",
                    Items =
                    [
                        new TemplateItem { Name = "Cafe Negro", Price = 1.50m, Aliases = ["cafe negro", "negro", "cafe", "café"] },
                        new TemplateItem { Name = "Cafe con Leche", Price = 2.00m, Aliases = ["con leche", "cafe con leche"] },
                        new TemplateItem { Name = "Capuccino", Price = 2.50m, Aliases = ["capuccino", "cappuccino"] },
                        new TemplateItem { Name = "Latte", Price = 2.50m, Aliases = ["latte"] },
                        new TemplateItem { Name = "Mocaccino", Price = 3.00m, Aliases = ["mocaccino", "mocha"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Postres",
                    Items =
                    [
                        new TemplateItem { Name = "Torta de Chocolate", Price = 3.50m, Aliases = ["torta chocolate", "chocolate cake", "torta"] },
                        new TemplateItem { Name = "Cheesecake", Price = 4.00m, Aliases = ["cheesecake", "cheese cake"] },
                        new TemplateItem { Name = "Brownie", Price = 2.50m, Aliases = ["brownie"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Snacks",
                    Items =
                    [
                        new TemplateItem { Name = "Croissant", Price = 2.00m, Aliases = ["croissant", "cruasan"] },
                        new TemplateItem { Name = "Sandwich de Jamon y Queso", Price = 3.50m, Aliases = ["sandwich", "sandwich jamon", "sándwich"] },
                        new TemplateItem { Name = "Empanada", Price = 2.00m, Aliases = ["empanada"] },
                    ]
                },
                new TemplateCategory
                {
                    Name = "Bebidas Frias",
                    Items =
                    [
                        new TemplateItem { Name = "Frappuccino", Price = 3.50m, Aliases = ["frappuccino", "frappe", "frapuccino"] },
                        new TemplateItem { Name = "Jugo Natural", Price = 2.50m, Aliases = ["jugo", "jugo natural"] },
                        new TemplateItem { Name = "Agua", Price = 1.00m, Aliases = ["agua"] },
                    ]
                }
            ],
            SuggestedUpsells = new()
            {
                ["cafes"] = ["postres", "snacks"],
                ["postres"] = ["cafes", "bebidas frias"],
                ["snacks"] = ["cafes", "bebidas frias"],
                ["bebidas frias"] = ["postres", "snacks"],
            }
        },
    };

    public static RestaurantTemplate? Get(string? type)
        => type is not null && All.TryGetValue(type, out var t) ? t : null;

    public static IReadOnlyList<object> ListSummaries()
        => All.Values.Select(t => new
        {
            t.Id,
            t.Name,
            t.Description,
            Categories = t.DefaultCategories.Select(c => c.Name).ToList()
        }).ToList();

    public static object? GetDetailedPreview(string? type)
    {
        var t = Get(type);
        if (t is null) return null;

        return new
        {
            t.Id,
            t.Name,
            t.Description,
            Categories = t.DefaultCategories.Select(c => new
            {
                c.Name,
                Items = c.Items.Select(i => new
                {
                    i.Name,
                    i.Price,
                    i.Aliases
                }).ToList()
            }).ToList(),
            TotalItems = t.DefaultCategories.Sum(c => c.Items.Count),
            UpsellPairings = t.SuggestedUpsells
        };
    }
}
