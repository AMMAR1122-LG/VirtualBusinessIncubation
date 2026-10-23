using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Web.Mvc;
using VirtualBusinessIncubation.Models;

namespace VirtualBusinessIncubation.Controllers
{
    // Read-only services browsing. No schema changes, authentication or payment writes.
    public class ServicesController : Controller
    {
        private const int ItemsPerPage = 9;
        private const string ConnectionName = "RozgarHubConnectionString";

        private const string BrowseFrom = @"
FROM dbo.profile_gigs AS g
INNER JOIN dbo.categories AS c ON c.id = g.category_id
INNER JOIN dbo.service_providers AS p ON p.id = g.provider_id
INNER JOIN dbo.users AS u ON u.id = p.user_id
WHERE g.availability_status = N'available'
  AND (@categoryId IS NULL OR g.category_id = @categoryId)
  AND (@maxRate IS NULL OR g.hourly_rate <= @maxRate)
  AND (@search IS NULL OR g.title LIKE @search ESCAPE N'~'
       OR ISNULL(g.description, N'') LIKE @search ESCAPE N'~'
       OR c.name LIKE @search ESCAPE N'~'
       OR ISNULL(p.skills, N'') LIKE @search ESCAPE N'~')";

        // Examples: /Services, /Services?q=web, /Services?categoryId=1&sort=price_asc&page=2
        [HttpGet]
        public ActionResult Index(string q, int? categoryId, decimal? maxRate, string sort = "recommended", int page = 1)
        {
            var model = new ServicesBrowseViewModel();
            model.Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (model.Query != null && model.Query.Length > 80)
                model.Query = model.Query.Substring(0, 80);
            model.CategoryId = categoryId.HasValue && categoryId.Value > 0 ? categoryId : null;
            model.MaxRate = maxRate.HasValue && maxRate.Value >= 0 && maxRate.Value <= 99999999m ? maxRate : null;
            model.Sort = IsAllowedSort(sort) ? sort : "recommended";
            model.Page = Math.Max(1, Math.Min(100000, page));
            model.PageSize = ItemsPerPage;

            try
            {
                using (var connection = OpenConnection())
                {
                    using (var command = new SqlCommand("SELECT c.id, c.name FROM dbo.categories AS c ORDER BY c.name", connection))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            model.Categories.Add(new ServiceCategoryItem
                            {
                                Id = reader.GetInt32(0), Name = reader.GetString(1)
                            });
                    }

                    using (var countCmd = new SqlCommand("SELECT COUNT(1) " + BrowseFrom, connection))
                    {
                        AddBrowseParameters(countCmd, model);
                        model.TotalCount = (int)countCmd.ExecuteScalar();
                    }
                    model.Page = Math.Min(model.Page, model.TotalPages);

                    string orderBy;
                    switch (model.Sort)
                    {
                        case "price_asc": orderBy = "g.hourly_rate ASC, g.id DESC"; break;
                        case "price_desc": orderBy = "g.hourly_rate DESC, g.id DESC"; break;
                        case "rating": orderBy = "g.average_rating DESC, g.id DESC"; break;
                        case "newest": orderBy = "g.id DESC"; break;
                        default: orderBy = "g.average_rating DESC, g.id DESC"; break;
                    }

                    const string projection = @"SELECT g.id, g.category_id, c.name AS category_name,
  g.title, g.description, g.hourly_rate, g.inspection_rate,
  g.average_rating, g.availability_status, g.image_url,
  u.name AS provider_name, p.urgent_hire ";
                    string sql = projection + BrowseFrom + " ORDER BY " + orderBy
                        + " OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY";

                    using (var listCmd = new SqlCommand(sql, connection))
                    {
                        AddBrowseParameters(listCmd, model);
                        listCmd.Parameters.Add("@offset", SqlDbType.Int).Value = (model.Page - 1) * ItemsPerPage;
                        listCmd.Parameters.Add("@pageSize", SqlDbType.Int).Value = ItemsPerPage;
                        using (var reader = listCmd.ExecuteReader())
                        {
                            while (reader.Read()) model.Services.Add(ReadService(reader));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("RozgarHub Services Index failed: " + ex);
                model.ErrorMessage = "Services could not be loaded. Check the SQL Server connection and try again.";
            }

            return View(model);
        }

        [HttpGet]
        public ActionResult Details(int? id)
        {
            if (!id.HasValue || id.Value < 1) return HttpNotFound();

            try
            {
                using (var connection = OpenConnection())
                {
                    var model = new ServiceDetailsViewModel();
                    const string detailsSql = @"
SELECT g.id, g.category_id, c.name AS category_name,
  g.title, g.description, g.hourly_rate, g.inspection_rate,
  g.average_rating, g.availability_status, g.image_url,
  u.name AS provider_name, p.urgent_hire,
  p.bio AS provider_bio, p.skills AS provider_skills,
  p.average_rating AS provider_rating
FROM dbo.profile_gigs AS g
INNER JOIN dbo.categories AS c ON c.id = g.category_id
INNER JOIN dbo.service_providers AS p ON p.id = g.provider_id
INNER JOIN dbo.users AS u ON u.id = p.user_id
WHERE g.id = @id";

                    using (var command = new SqlCommand(detailsSql, connection))
                    {
                        command.Parameters.Add("@id", SqlDbType.Int).Value = id.Value;
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read()) return HttpNotFound();
                            model.Service = ReadService(reader);
                            model.ProviderBio = GetNullableString(reader, "provider_bio");
                            model.ProviderSkills = GetNullableString(reader, "provider_skills");
                            model.ProviderRating = reader.GetDecimal(reader.GetOrdinal("provider_rating"));
                        }
                    }

                    // Display reviews belonging to completed orders for this specific gig.
                    const string reviewsSql = @"
SELECT TOP (6) r.rating, r.comment, r.created_at,
  u.name AS customer_name
FROM dbo.reviews AS r
INNER JOIN dbo.orders AS o ON o.id = r.order_id
INNER JOIN dbo.customers AS cu ON cu.id = o.customer_id
INNER JOIN dbo.users AS u ON u.id = cu.user_id
WHERE o.profile_gig_id = @id AND o.status = N'completed'
ORDER BY r.created_at DESC, r.id DESC";
                    using (var command = new SqlCommand(reviewsSql, connection))
                    {
                        command.Parameters.Add("@id", SqlDbType.Int).Value = id.Value;
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                model.Reviews.Add(new ServiceReviewItem
                                {
                                    Rating = reader.GetInt16(0),
                                    Comment = reader.IsDBNull(1) ? null : reader.GetString(1),
                                    CreatedAt = reader.GetDateTime(2),
                                    CustomerName = reader.GetString(3)
                                });
                            }
                        }
                    }

                    using (var command = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.reviews AS r
INNER JOIN dbo.orders AS o ON o.id = r.order_id
WHERE o.profile_gig_id = @id AND o.status = N'completed'", connection))
                    {
                        command.Parameters.Add("@id", SqlDbType.Int).Value = id.Value;
                        model.TotalReviews = (int)command.ExecuteScalar();
                    }

                    const string relatedSql = @"
SELECT TOP (3) g.id, g.category_id, c.name AS category_name,
  g.title, g.description, g.hourly_rate, g.inspection_rate,
  g.average_rating, g.availability_status, g.image_url,
  u.name AS provider_name, p.urgent_hire
FROM dbo.profile_gigs AS g
INNER JOIN dbo.categories AS c ON c.id = g.category_id
INNER JOIN dbo.service_providers AS p ON p.id = g.provider_id
INNER JOIN dbo.users AS u ON u.id = p.user_id
WHERE g.category_id = @categoryId AND g.id <> @id
AND g.availability_status = N'available'
ORDER BY g.average_rating DESC, g.id DESC";
                    using (var command = new SqlCommand(relatedSql, connection))
                    {
                        command.Parameters.Add("@categoryId", SqlDbType.Int).Value = model.Service.CategoryId;
                        command.Parameters.Add("@id", SqlDbType.Int).Value = id.Value;
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read()) model.RelatedServices.Add(ReadService(reader));
                        }
                    }
                    return View(model);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("RozgarHub Services Details failed: " + ex);
                return new HttpStatusCodeResult(503, "Service information is temporarily unavailable");
            }
        }

        private static SqlConnection OpenConnection()
        {
            var settings = ConfigurationManager.ConnectionStrings[ConnectionName];
            if (settings == null || string.IsNullOrWhiteSpace(settings.ConnectionString))
                throw new ConfigurationErrorsException("Missing connection string: " + ConnectionName);
            var connection = new SqlConnection(settings.ConnectionString);
            connection.Open();
            return connection;
        }

        private static void AddBrowseParameters(SqlCommand command, ServicesBrowseViewModel model)
        {
            command.Parameters.Add("@categoryId", SqlDbType.Int).Value =
                (object)model.CategoryId ?? DBNull.Value;
            var price = command.Parameters.Add("@maxRate", SqlDbType.Decimal);
            price.Precision = 10;
            price.Scale = 2;
            price.Value = (object)model.MaxRate ?? DBNull.Value;
            command.Parameters.Add("@search", SqlDbType.NVarChar, 300).Value = model.Query == null
                ? (object)DBNull.Value : "%" + EscapeLike(model.Query) + "%";
        }

        // Escape SQL LIKE wildcard chars; all inputs are passed as SQL parameters.
        private static string EscapeLike(string value)
        {
            return value.Replace("~", "~~").Replace("%", "~%")
                .Replace("_", "~_").Replace("[", "~[");
        }

        private static bool IsAllowedSort(string sort)
        {
            return sort == "recommended" || sort == "price_asc" || sort == "price_desc"
                || sort == "rating" || sort == "newest";
        }

        private static string GetNullableString(SqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static ServiceListItem ReadService(SqlDataReader reader)
        {
            return new ServiceListItem
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                CategoryId = reader.GetInt32(reader.GetOrdinal("category_id")),
                CategoryName = reader.GetString(reader.GetOrdinal("category_name")),
                Title = reader.GetString(reader.GetOrdinal("title")),
                Description = GetNullableString(reader, "description"),
                HourlyRate = reader.GetDecimal(reader.GetOrdinal("hourly_rate")),
                InspectionRate = reader.GetDecimal(reader.GetOrdinal("inspection_rate")),
                AverageRating = reader.GetDecimal(reader.GetOrdinal("average_rating")),
                AvailabilityStatus = reader.GetString(reader.GetOrdinal("availability_status")),
                ImageUrl = SafeImageUrl(GetNullableString(reader, "image_url")),
                ProviderName = reader.GetString(reader.GetOrdinal("provider_name")),
                UrgentHire = reader.GetBoolean(reader.GetOrdinal("urgent_hire"))
            };
        }

        // Permit local site image paths or HTTPS. Reject javascript:, data:, and protocol-relative URLs.
        private static string SafeImageUrl(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            source = source.Trim();
            if (source.StartsWith("~/", StringComparison.Ordinal) ||
                (source.StartsWith("/", StringComparison.Ordinal) && !source.StartsWith("//", StringComparison.Ordinal)))
                return source;
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri) &&
                uri.Scheme == Uri.UriSchemeHttps) return source;
            return null;
        }
    }
}
