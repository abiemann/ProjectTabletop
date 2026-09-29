using System.Globalization;

namespace ProjectTabletop.Interaction;

/// <summary>
/// The Globe's starting view: the reference city of the computer's time zone, from the
/// public-domain tz database zone.tab. No location service or network is used.
/// </summary>
public static class GlobeHome
{
    /// <summary>Reference-city latitude and longitude (east positive), or null for zones
    /// without a city such as Etc/GMT+8.</summary>
    /// <param name="region">Two-letter Windows region; it selects the regional city of a
    /// shared Windows zone, e.g. Vancouver rather than Los Angeles for Pacific time in Canada.</param>
    public static (double Latitude, double Longitude)? ReferenceCity(TimeZoneInfo zone, string? region = null)
    {
        string? iana = zone.HasIanaId ? zone.Id :
            !string.IsNullOrEmpty(region) && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, region, out var regional)
                ? regional : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var golden) ? golden : null;
        if (iana is null) return null;
        if (LegacyNames.TryGetValue(iana, out var current)) iana = current;
        return Cities.Value.TryGetValue(iana, out var city) ? city : null;
    }

    // Windows' CLDR mapping still returns some pre-rename tz names.
    private static readonly Dictionary<string, string> LegacyNames = new(StringComparer.Ordinal)
    {
        ["America/Buenos_Aires"] = "America/Argentina/Buenos_Aires", ["America/Catamarca"] = "America/Argentina/Catamarca",
        ["America/Cordoba"] = "America/Argentina/Cordoba", ["America/Jujuy"] = "America/Argentina/Jujuy",
        ["America/Mendoza"] = "America/Argentina/Mendoza", ["America/Coral_Harbour"] = "America/Atikokan",
        ["America/Godthab"] = "America/Nuuk", ["America/Indianapolis"] = "America/Indiana/Indianapolis",
        ["America/Louisville"] = "America/Kentucky/Louisville", ["Asia/Calcutta"] = "Asia/Kolkata",
        ["Asia/Katmandu"] = "Asia/Kathmandu", ["Asia/Rangoon"] = "Asia/Yangon", ["Asia/Saigon"] = "Asia/Ho_Chi_Minh",
        ["Atlantic/Faeroe"] = "Atlantic/Faroe", ["Europe/Kiev"] = "Europe/Kyiv", ["Pacific/Enderbury"] = "Pacific/Kanton",
        ["Pacific/Ponape"] = "Pacific/Pohnpei", ["Pacific/Truk"] = "Pacific/Chuuk",
        ["Africa/Asmera"] = "Africa/Asmara", ["Pacific/Johnston"] = "Pacific/Honolulu"
    };

    private static readonly Lazy<Dictionary<string, (double Latitude, double Longitude)>> Cities = new(() =>
        ZoneTab.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' '))
            .ToDictionary(fields => fields[0], fields => ParseIso6709(fields[1]), StringComparer.Ordinal));

    // ±DDMM±DDDMM or ±DDMMSS±DDDMMSS.
    private static (double Latitude, double Longitude) ParseIso6709(string value)
    {
        int split = value.IndexOfAny(['+', '-'], 1);
        return (Angle(value[..split], 2), Angle(value[split..], 3));
        static double Angle(string text, int degreeDigits)
        {
            double sign = text[0] == '-' ? -1 : 1;
            string digits = text[1..];
            double degrees = int.Parse(digits[..degreeDigits], CultureInfo.InvariantCulture);
            double minutes = int.Parse(digits.Substring(degreeDigits, 2), CultureInfo.InvariantCulture);
            double seconds = digits.Length > degreeDigits + 2
                ? int.Parse(digits[(degreeDigits + 2)..], CultureInfo.InvariantCulture) : 0;
            return sign * (degrees + minutes / 60 + seconds / 3600);
        }
    }

    // Zone name and coordinates columns of tzdb zone.tab (public domain).
    private const string ZoneTab = """
        Europe/Andorra +4230+00131
        Asia/Dubai +2518+05518
        Asia/Kabul +3431+06912
        America/Antigua +1703-06148
        America/Anguilla +1812-06304
        Europe/Tirane +4120+01950
        Asia/Yerevan +4011+04430
        Africa/Luanda -0848+01314
        Antarctica/McMurdo -7750+16636
        Antarctica/Casey -6617+11031
        Antarctica/Davis -6835+07758
        Antarctica/DumontDUrville -6640+14001
        Antarctica/Mawson -6736+06253
        Antarctica/Palmer -6448-06406
        Antarctica/Rothera -6734-06808
        Antarctica/Syowa -690022+0393524
        Antarctica/Troll -720041+0023206
        Antarctica/Vostok -7824+10654
        America/Argentina/Buenos_Aires -3436-05827
        America/Argentina/Cordoba -3124-06411
        America/Argentina/Salta -2447-06525
        America/Argentina/Jujuy -2411-06518
        America/Argentina/Tucuman -2649-06513
        America/Argentina/Catamarca -2828-06547
        America/Argentina/La_Rioja -2926-06651
        America/Argentina/San_Juan -3132-06831
        America/Argentina/Mendoza -3253-06849
        America/Argentina/San_Luis -3319-06621
        America/Argentina/Rio_Gallegos -5138-06913
        America/Argentina/Ushuaia -5448-06818
        Pacific/Pago_Pago -1416-17042
        Europe/Vienna +4813+01620
        Australia/Lord_Howe -3133+15905
        Antarctica/Macquarie -5430+15857
        Australia/Hobart -4253+14719
        Australia/Melbourne -3749+14458
        Australia/Sydney -3352+15113
        Australia/Broken_Hill -3157+14127
        Australia/Brisbane -2728+15302
        Australia/Lindeman -2016+14900
        Australia/Adelaide -3455+13835
        Australia/Darwin -1228+13050
        Australia/Perth -3157+11551
        Australia/Eucla -3143+12852
        America/Aruba +1230-06958
        Europe/Mariehamn +6006+01957
        Asia/Baku +4023+04951
        Europe/Sarajevo +4352+01825
        America/Barbados +1306-05937
        Asia/Dhaka +2343+09025
        Europe/Brussels +5050+00420
        Africa/Ouagadougou +1222-00131
        Europe/Sofia +4241+02319
        Asia/Bahrain +2623+05035
        Africa/Bujumbura -0323+02922
        Africa/Porto-Novo +0629+00237
        America/St_Barthelemy +1753-06251
        Atlantic/Bermuda +3217-06446
        Asia/Brunei +0456+11455
        America/La_Paz -1630-06809
        America/Kralendijk +120903-0681636
        America/Noronha -0351-03225
        America/Belem -0127-04829
        America/Fortaleza -0343-03830
        America/Recife -0803-03454
        America/Araguaina -0712-04812
        America/Maceio -0940-03543
        America/Bahia -1259-03831
        America/Sao_Paulo -2332-04637
        America/Campo_Grande -2027-05437
        America/Cuiaba -1535-05605
        America/Santarem -0226-05452
        America/Porto_Velho -0846-06354
        America/Boa_Vista +0249-06040
        America/Manaus -0308-06001
        America/Eirunepe -0640-06952
        America/Rio_Branco -0958-06748
        America/Nassau +2505-07721
        Asia/Thimphu +2728+08939
        Africa/Gaborone -2439+02555
        Europe/Minsk +5354+02734
        America/Belize +1730-08812
        America/St_Johns +4734-05243
        America/Halifax +4439-06336
        America/Glace_Bay +4612-05957
        America/Moncton +4606-06447
        America/Goose_Bay +5320-06025
        America/Blanc-Sablon +5125-05707
        America/Toronto +4339-07923
        America/Iqaluit +6344-06828
        America/Atikokan +484531-0913718
        America/Winnipeg +4953-09709
        America/Resolute +744144-0944945
        America/Rankin_Inlet +624900-0920459
        America/Regina +5024-10439
        America/Swift_Current +5017-10750
        America/Edmonton +5333-11328
        America/Cambridge_Bay +690650-1050310
        America/Inuvik +682059-1334300
        America/Vancouver +4916-12307
        America/Creston +4906-11631
        America/Dawson_Creek +5546-12014
        America/Fort_Nelson +5848-12242
        America/Whitehorse +6043-13503
        America/Dawson +6404-13925
        Indian/Cocos -1210+09655
        Africa/Kinshasa -0418+01518
        Africa/Lubumbashi -1140+02728
        Africa/Bangui +0422+01835
        Africa/Brazzaville -0416+01517
        Europe/Zurich +4723+00832
        Africa/Abidjan +0519-00402
        Pacific/Rarotonga -2114-15946
        America/Santiago -3327-07040
        America/Coyhaique -4534-07204
        America/Punta_Arenas -5309-07055
        Pacific/Easter -2709-10926
        Africa/Douala +0403+00942
        Asia/Shanghai +3114+12128
        Asia/Urumqi +4348+08735
        America/Bogota +0436-07405
        America/Costa_Rica +0956-08405
        America/Havana +2308-08222
        Atlantic/Cape_Verde +1455-02331
        America/Curacao +1211-06900
        Indian/Christmas -1025+10543
        Asia/Nicosia +3510+03322
        Asia/Famagusta +3507+03357
        Europe/Prague +5005+01426
        Europe/Berlin +5230+01322
        Europe/Busingen +4742+00841
        Africa/Djibouti +1136+04309
        Europe/Copenhagen +5540+01235
        America/Dominica +1518-06124
        America/Santo_Domingo +1828-06954
        Africa/Algiers +3647+00303
        America/Guayaquil -0210-07950
        Pacific/Galapagos -0054-08936
        Europe/Tallinn +5925+02445
        Africa/Cairo +3003+03115
        Africa/El_Aaiun +2709-01312
        Africa/Asmara +1520+03853
        Europe/Madrid +4024-00341
        Africa/Ceuta +3553-00519
        Atlantic/Canary +2806-01524
        Africa/Addis_Ababa +0902+03842
        Europe/Helsinki +6010+02458
        Pacific/Fiji -1808+17825
        Atlantic/Stanley -5142-05751
        Pacific/Chuuk +0725+15147
        Pacific/Pohnpei +0658+15813
        Pacific/Kosrae +0519+16259
        Atlantic/Faroe +6201-00646
        Europe/Paris +4852+00220
        Africa/Libreville +0023+00927
        Europe/London +513030-0000731
        America/Grenada +1203-06145
        Asia/Tbilisi +4143+04449
        America/Cayenne +0456-05220
        Europe/Guernsey +492717-0023210
        Africa/Accra +0533-00013
        Europe/Gibraltar +3608-00521
        America/Nuuk +6411-05144
        America/Danmarkshavn +7646-01840
        America/Scoresbysund +7029-02158
        America/Thule +7634-06847
        Africa/Banjul +1328-01639
        Africa/Conakry +0931-01343
        America/Guadeloupe +1614-06132
        Africa/Malabo +0345+00847
        Europe/Athens +3758+02343
        Atlantic/South_Georgia -5416-03632
        America/Guatemala +1438-09031
        Pacific/Guam +1328+14445
        Africa/Bissau +1151-01535
        America/Guyana +0648-05810
        Asia/Hong_Kong +2217+11409
        America/Tegucigalpa +1406-08713
        Europe/Zagreb +4548+01558
        America/Port-au-Prince +1832-07220
        Europe/Budapest +4730+01905
        Asia/Jakarta -0610+10648
        Asia/Pontianak -0002+10920
        Asia/Makassar -0507+11924
        Asia/Jayapura -0232+14042
        Europe/Dublin +5320-00615
        Asia/Jerusalem +314650+0351326
        Europe/Isle_of_Man +5409-00428
        Asia/Kolkata +2232+08822
        Indian/Chagos -0720+07225
        Asia/Baghdad +3321+04425
        Asia/Tehran +3540+05126
        Atlantic/Reykjavik +6409-02151
        Europe/Rome +4154+01229
        Europe/Jersey +491101-0020624
        America/Jamaica +175805-0764736
        Asia/Amman +3157+03556
        Asia/Tokyo +353916+1394441
        Africa/Nairobi -0117+03649
        Asia/Bishkek +4254+07436
        Asia/Phnom_Penh +1133+10455
        Pacific/Tarawa +0125+17300
        Pacific/Kanton -0247-17143
        Pacific/Kiritimati +0152-15720
        Indian/Comoro -1141+04316
        America/St_Kitts +1718-06243
        Asia/Pyongyang +3901+12545
        Asia/Seoul +3733+12658
        Asia/Kuwait +2920+04759
        America/Cayman +1918-08123
        Asia/Almaty +4315+07657
        Asia/Qyzylorda +4448+06528
        Asia/Qostanay +5312+06337
        Asia/Aqtobe +5017+05710
        Asia/Aqtau +4431+05016
        Asia/Atyrau +4707+05156
        Asia/Oral +5113+05121
        Asia/Vientiane +1758+10236
        Asia/Beirut +3353+03530
        America/St_Lucia +1401-06100
        Europe/Vaduz +4709+00931
        Asia/Colombo +0656+07951
        Africa/Monrovia +0618-01047
        Africa/Maseru -2928+02730
        Europe/Vilnius +5441+02519
        Europe/Luxembourg +4936+00609
        Europe/Riga +5657+02406
        Africa/Tripoli +3254+01311
        Africa/Casablanca +3339-00735
        Europe/Monaco +4342+00723
        Europe/Chisinau +4700+02850
        Europe/Podgorica +4226+01916
        America/Marigot +1804-06305
        Indian/Antananarivo -1855+04731
        Pacific/Majuro +0709+17112
        Pacific/Kwajalein +0905+16720
        Europe/Skopje +4159+02126
        Africa/Bamako +1239-00800
        Asia/Yangon +1647+09610
        Asia/Ulaanbaatar +4755+10653
        Asia/Hovd +4801+09139
        Asia/Macau +221150+1133230
        Pacific/Saipan +1512+14545
        America/Martinique +1436-06105
        Africa/Nouakchott +1806-01557
        America/Montserrat +1643-06213
        Europe/Malta +3554+01431
        Indian/Mauritius -2010+05730
        Indian/Maldives +0410+07330
        Africa/Blantyre -1547+03500
        America/Mexico_City +1924-09909
        America/Cancun +2105-08646
        America/Merida +2058-08937
        America/Monterrey +2540-10019
        America/Matamoros +2550-09730
        America/Chihuahua +2838-10605
        America/Ciudad_Juarez +3144-10629
        America/Ojinaga +2934-10425
        America/Mazatlan +2313-10625
        America/Bahia_Banderas +2048-10515
        America/Hermosillo +2904-11058
        America/Tijuana +3232-11701
        Asia/Kuala_Lumpur +0310+10142
        Asia/Kuching +0133+11020
        Africa/Maputo -2558+03235
        Africa/Windhoek -2234+01706
        Pacific/Noumea -2216+16627
        Africa/Niamey +1331+00207
        Pacific/Norfolk -2903+16758
        Africa/Lagos +0627+00324
        America/Managua +1209-08617
        Europe/Amsterdam +5222+00454
        Europe/Oslo +5955+01045
        Asia/Kathmandu +2743+08519
        Pacific/Nauru -0031+16655
        Pacific/Niue -1901-16955
        Pacific/Auckland -3652+17446
        Pacific/Chatham -4357-17633
        Asia/Muscat +2336+05835
        America/Panama +0858-07932
        America/Lima -1203-07703
        Pacific/Tahiti -1732-14934
        Pacific/Marquesas -0900-13930
        Pacific/Gambier -2308-13457
        Pacific/Port_Moresby -0930+14710
        Pacific/Bougainville -0613+15534
        Asia/Manila +143512+1205804
        Asia/Karachi +2452+06703
        Europe/Warsaw +5215+02100
        America/Miquelon +4703-05620
        Pacific/Pitcairn -2504-13005
        America/Puerto_Rico +182806-0660622
        Asia/Gaza +3130+03428
        Asia/Hebron +313200+0350542
        Europe/Lisbon +3843-00908
        Atlantic/Madeira +3238-01654
        Atlantic/Azores +3744-02540
        Pacific/Palau +0720+13429
        America/Asuncion -2516-05740
        Asia/Qatar +2517+05132
        Indian/Reunion -2052+05528
        Europe/Bucharest +4426+02606
        Europe/Belgrade +4450+02030
        Europe/Kaliningrad +5443+02030
        Europe/Moscow +554521+0373704
        Europe/Simferopol +4457+03406
        Europe/Kirov +5836+04939
        Europe/Volgograd +4844+04425
        Europe/Astrakhan +4621+04803
        Europe/Saratov +5134+04602
        Europe/Ulyanovsk +5420+04824
        Europe/Samara +5312+05009
        Asia/Yekaterinburg +5651+06036
        Asia/Omsk +5500+07324
        Asia/Novosibirsk +5502+08255
        Asia/Barnaul +5322+08345
        Asia/Tomsk +5630+08458
        Asia/Novokuznetsk +5345+08707
        Asia/Krasnoyarsk +5601+09250
        Asia/Irkutsk +5216+10420
        Asia/Chita +5203+11328
        Asia/Yakutsk +6200+12940
        Asia/Khandyga +623923+1353314
        Asia/Vladivostok +4310+13156
        Asia/Ust-Nera +643337+1431336
        Asia/Magadan +5934+15048
        Asia/Sakhalin +4658+14242
        Asia/Srednekolymsk +6728+15343
        Asia/Kamchatka +5301+15839
        Asia/Anadyr +6445+17729
        Africa/Kigali -0157+03004
        Asia/Riyadh +2438+04643
        Pacific/Guadalcanal -0932+16012
        Indian/Mahe -0440+05528
        Africa/Khartoum +1536+03232
        Europe/Stockholm +5920+01803
        Asia/Singapore +0117+10351
        Atlantic/St_Helena -1555-00542
        Europe/Ljubljana +4603+01431
        Arctic/Longyearbyen +7800+01600
        Europe/Bratislava +4809+01707
        Africa/Freetown +0830-01315
        Europe/San_Marino +4355+01228
        Africa/Dakar +1440-01726
        Africa/Mogadishu +0204+04522
        America/Paramaribo +0550-05510
        Africa/Juba +0451+03137
        Africa/Sao_Tome +0020+00644
        America/El_Salvador +1342-08912
        America/Lower_Princes +180305-0630250
        Asia/Damascus +3330+03618
        Africa/Mbabane -2618+03106
        America/Grand_Turk +2128-07108
        Africa/Ndjamena +1207+01503
        Indian/Kerguelen -492110+0701303
        Africa/Lome +0608+00113
        Asia/Bangkok +1345+10031
        Asia/Dushanbe +3835+06848
        Pacific/Fakaofo -0922-17114
        Asia/Dili -0833+12535
        Asia/Ashgabat +3757+05823
        Africa/Tunis +3648+01011
        Pacific/Tongatapu -210800-1751200
        Europe/Istanbul +4101+02858
        America/Port_of_Spain +1039-06131
        Pacific/Funafuti -0831+17913
        Asia/Taipei +2503+12130
        Africa/Dar_es_Salaam -0648+03917
        Europe/Kyiv +5026+03031
        Africa/Kampala +0019+03225
        Pacific/Midway +2813-17722
        Pacific/Wake +1917+16637
        America/New_York +404251-0740023
        America/Detroit +421953-0830245
        America/Kentucky/Louisville +381515-0854534
        America/Kentucky/Monticello +364947-0845057
        America/Indiana/Indianapolis +394606-0860929
        America/Indiana/Vincennes +384038-0873143
        America/Indiana/Winamac +410305-0863611
        America/Indiana/Marengo +382232-0862041
        America/Indiana/Petersburg +382931-0871643
        America/Indiana/Vevay +384452-0850402
        America/Chicago +415100-0873900
        America/Indiana/Tell_City +375711-0864541
        America/Indiana/Knox +411745-0863730
        America/Menominee +450628-0873651
        America/North_Dakota/Center +470659-1011757
        America/North_Dakota/New_Salem +465042-1012439
        America/North_Dakota/Beulah +471551-1014640
        America/Denver +394421-1045903
        America/Boise +433649-1161209
        America/Phoenix +332654-1120424
        America/Los_Angeles +340308-1181434
        America/Anchorage +611305-1495401
        America/Juneau +581807-1342511
        America/Sitka +571035-1351807
        America/Metlakatla +550737-1313435
        America/Yakutat +593249-1394338
        America/Nome +643004-1652423
        America/Adak +515248-1763929
        Pacific/Honolulu +211825-1575130
        America/Montevideo -345433-0561245
        Asia/Samarkand +3940+06648
        Asia/Tashkent +4120+06918
        Europe/Vatican +415408+0122711
        America/St_Vincent +1309-06114
        America/Caracas +1030-06656
        America/Tortola +1827-06437
        America/St_Thomas +1821-06456
        Asia/Ho_Chi_Minh +1045+10640
        Pacific/Efate -1740+16825
        Pacific/Wallis -1318-17610
        Pacific/Apia -1350-17144
        Asia/Aden +1245+04512
        Indian/Mayotte -1247+04514
        Africa/Johannesburg -2615+02800
        Africa/Lusaka -1525+02817
        Africa/Harare -1750+03103
        """;
}
