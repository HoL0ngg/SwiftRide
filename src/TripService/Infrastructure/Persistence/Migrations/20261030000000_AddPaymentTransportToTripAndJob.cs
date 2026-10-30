using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.TripService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTransportToTripAndJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentTransport",
                table: "Trips",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Rest");

            migrationBuilder.AddColumn<string>(
                name: "Transport",
                table: "TripPaymentJobs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Rest");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentTransport",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "Transport",
                table: "TripPaymentJobs");
        }
    }
}
