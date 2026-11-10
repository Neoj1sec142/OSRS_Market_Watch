using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketArchiveLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketArchiveExports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ArchiveFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TransferredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CommittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchiveSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ArchiveSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    ManifestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StorageLocation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketArchiveExports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketArchiveSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketArchiveSources", x => x.Id);
                    table.CheckConstraint("CK_MarketArchiveSources_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "MarketArchiveStreamRanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArchiveExportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stream = table.Column<int>(type: "INTEGER", nullable: false),
                    StartIdExclusive = table.Column<long>(type: "INTEGER", nullable: true),
                    EndIdInclusive = table.Column<long>(type: "INTEGER", nullable: true),
                    StartUtcInclusive = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndUtcExclusive = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecordCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketArchiveStreamRanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketArchiveStreamRanges_MarketArchiveExports_ArchiveExportId",
                        column: x => x.ArchiveExportId,
                        principalTable: "MarketArchiveExports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketArchiveExports_SourceId_ArchiveFileName",
                table: "MarketArchiveExports",
                columns: new[] { "SourceId", "ArchiveFileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketArchiveExports_SourceId_Status_CreatedAtUtc",
                table: "MarketArchiveExports",
                columns: new[] { "SourceId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketArchiveSources_SourceId",
                table: "MarketArchiveSources",
                column: "SourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketArchiveStreamRanges_ArchiveExportId_Stream",
                table: "MarketArchiveStreamRanges",
                columns: new[] { "ArchiveExportId", "Stream" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketArchiveSources");

            migrationBuilder.DropTable(
                name: "MarketArchiveStreamRanges");

            migrationBuilder.DropTable(
                name: "MarketArchiveExports");
        }
    }
}
