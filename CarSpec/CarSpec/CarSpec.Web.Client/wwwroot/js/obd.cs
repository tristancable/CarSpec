window.obd = {
    async connect()
    {
        console.log("Simulated OBD connection established.");
    },

    async disconnect()
    {
        console.log("Disconnected from OBD device.");
    },

    async getData()
    {
        return {
        speed: Math.random() * 100,
            rpm: Math.random() * 7000,
            throttlePercent: Math.random() * 100,
            fuelLevelPercent: 50 + Math.random() * 50,
            oilTempF: 180 + Math.random() * 30,
            coolantTempF: 170 + Math.random() * 25,
            intakeTempF: 70 + Math.random() * 10,
            lastUpdated: new Date().toISOString()
        }
        ;
    }
}
;