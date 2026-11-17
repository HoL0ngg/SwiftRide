using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.TripService.Infrastructure.Persistence.Migrations;

public partial class AddVehicleTypeToTrip : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "VehicleType",
            table: "Trips",
            type: "nvarchar(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "motorbike");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "VehicleType", table: "Trips");
    }
}
