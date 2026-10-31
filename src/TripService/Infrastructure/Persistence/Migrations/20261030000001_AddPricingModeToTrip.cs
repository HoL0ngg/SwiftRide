using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.TripService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingModeToTrip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PricingMode",
                table: "Trips",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "standard");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PricingMode",
                table: "Trips");
        }
    }
}
