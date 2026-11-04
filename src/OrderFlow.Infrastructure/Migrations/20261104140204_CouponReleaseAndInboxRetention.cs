using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CouponReleaseAndInboxRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleasedAtUtc",
                table: "CouponUsages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ProcessedAtUtc",
                table: "InboxMessages",
                column: "ProcessedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxMessages_ProcessedAtUtc",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "ReleasedAtUtc",
                table: "CouponUsages");
        }
    }
}
