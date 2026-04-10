<%@ Page Language="C#" Inherits="System.Web.Mvc.ViewPage" ContentType="text/xml" %>
<%@ Import Namespace="Citrix.DeliveryServices.PnaProtocol.Resources.Controllers" %>
<% string pnaBaseUrl = ViewData[PnaConfigViewConstants.StoreBaseUrlId] + "/PNAgent";
%><?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE PNAgent_Configuration SYSTEM "PNAgent.dtd">
<PNAgent_Configuration xmlns:xsi="http://www.w3.org/2000/10/XMLSchema-instance" >
    <FolderDisplay>
        <StartMenuDisplay>
            <Enabled modifiable="true" forcedefault="false">true</Enabled>
            <RootFolder root="programs" modifiable="true" forcedefault="false"></RootFolder>
        </StartMenuDisplay>
        <DesktopDisplay>
            <Enabled modifiable="true" forcedefault="false">false</Enabled>
            <Icon>
                <Name modifiable="true" forcedefault="false"></Name>
            </Icon>
        </DesktopDisplay>
        <SystemTrayMenuDisplay>
            <Enabled modifiable="true" forcedefault="false">true</Enabled>
        </SystemTrayMenuDisplay>
    </FolderDisplay>
    <DesktopIntegration>
        <StartMenu>add</StartMenu>
        <Desktop>add</Desktop>
    </DesktopIntegration>
    <ConfigurationFile>
        <Location modifiable="true" forcedefault="false" replaceServerLocation="true"><%= pnaBaseUrl %>/config.xml</Location>
        <Refresh>
            <Poll>
                <Enabled>false</Enabled>
                <Period>8</Period>
            </Poll>
        </Refresh>
    </ConfigurationFile>
    <Request>
        <Enumeration>
            <Location replaceServerLocation="true"><%= pnaBaseUrl %>/enum.aspx</Location>
            <Smartcard_Location replaceServerLocation="true"><%= pnaBaseUrl %>/Smartcard/enum.aspx</Smartcard_Location>
            <Integrated_Location replaceServerLocation="true"><%= pnaBaseUrl%>/Integrated/enum.aspx</Integrated_Location>
            <Refresh>
                <OnApplicationStart modifiable="true" forcedefault="false">true</OnApplicationStart>
                <OnResourceRequest modifiable="true" forcedefault="false">false</OnResourceRequest>
                <Poll modifiable="true" forcedefault="false">
                    <Enabled>true</Enabled>
                    <Period>6</Period>
                </Poll>
            </Refresh>
        </Enumeration>
        <Change_Password>
<% if (ViewData.ContainsKey(PnaConfigViewConstants.ChangePasswordUrlId)) { %>
             <Location replaceServerLocation="true"><%= ViewData[PnaConfigViewConstants.ChangePasswordUrlId]%></Location>
<% } %>
        </Change_Password>
        <Resource>
            <Location replaceServerLocation="true"><%= pnaBaseUrl %>/launch.aspx</Location>
            <Smartcard_Location replaceServerLocation="true"><%= pnaBaseUrl %>/Smartcard/launch.aspx</Smartcard_Location>
            <Integrated_Location replaceServerLocation="true"><%= pnaBaseUrl %>/Integrated/launch.aspx</Integrated_Location>
        </Resource>
       <Reconnect>
            <Location replaceServerLocation="true"><%= pnaBaseUrl %>/reconnect.aspx</Location>
            <Smartcard_Location replaceServerLocation="true"><%= pnaBaseUrl %>/Smartcard/reconnect.aspx</Smartcard_Location>
            <Integrated_Location replaceServerLocation="true"><%= pnaBaseUrl %>/Integrated/reconnect.aspx</Integrated_Location>
       </Reconnect>
        <MachineControl>
            <Location replaceServerLocation="true"><%= pnaBaseUrl %>/desktopcontrol.aspx</Location>
            <Smartcard_Location replaceServerLocation="true"><%= pnaBaseUrl %>/Smartcard/desktopcontrol.aspx</Smartcard_Location>
            <Integrated_Location replaceServerLocation="true"><%= pnaBaseUrl %>/Integrated/desktopcontrol.aspx</Integrated_Location>
        </MachineControl>
    </Request>
    <Failover>
    </Failover>
    <Logon>
        <LogonMethod><%= ViewData[PnaConfigViewConstants.LogonMethodId]%></LogonMethod>
        <EnableSavePassword>false</EnableSavePassword>
        <EnableKerberos><%= ViewData[PnaConfigViewConstants.EnableKerberosId] %></EnableKerberos>
        <SupportNDS>false</SupportNDS>
        <NDS_Settings>
            <DefaultTree></DefaultTree>
        </NDS_Settings>
    </Logon>
    <ChangePassword>
        <Allow><%= ViewData[PnaConfigViewConstants.ChangePasswordAllowedId] %></Allow>
        <Method><%= ViewData[PnaConfigViewConstants.ChangePasswordMethodId] %></Method>
    </ChangePassword>
    <UserInterface>
        <ServerSettings>true</ServerSettings>
        <FolderDisplaySettings>true</FolderDisplaySettings>
        <RefreshSettings>false</RefreshSettings>
        <ReconnectSettings>true</ReconnectSettings>
    </UserInterface>
    <ReconnectOptions>
        <ReconnectFromLogon forcedefault="false" modifiable="true">
            <Disconnected>true</Disconnected>
            <Active>true</Active>
        </ReconnectFromLogon>
        <ReconnectFromButton forcedefault="false" modifiable="true">
            <Disconnected>true</Disconnected>
            <Active>true</Active>
        </ReconnectFromButton>
    </ReconnectOptions>
    <FileCleanup>
        <Logoff>false</Logoff>
        <Exit>false</Exit>
        <RefreshApp directoryDepth="0">true</RefreshApp>
    </FileCleanup>
    <ICA_Options>
        <DisplaySize>
            <Options>
                <Dimension>
                    <Width>640</Width>
                    <Height>480</Height>
                </Dimension>
                <Dimension>
                    <Width>800</Width>
                    <Height>600</Height>
                </Dimension>
                <Dimension>
                    <Width>1024</Width>
                    <Height>768</Height>
                </Dimension>
                <Dimension>
                    <Width>1280</Width>
                    <Height>1024</Height>
                </Dimension>
                <Dimension>
                    <Width>1600</Width>
                    <Height>1200</Height>
                </Dimension>
                <Mode>seamless</Mode>
                <Mode>fullscreen</Mode>
            </Options>
        </DisplaySize>
        <ColorDepth>
            <Options>4</Options>
            <Options>8</Options>
        </ColorDepth>
        <Audio>
            <Options>high</Options>
            <Options>medium</Options>
            <Options>low</Options>
            <Options>off</Options>
        </Audio>
        <TransparentKeyPassthrough>
            <Options>Local</Options>
            <Options>Remote</Options>
            <Options>FullScreenOnly</Options>
        </TransparentKeyPassthrough>
        <SpecialFolderRedirection modifiable="true">
            <Enabled>false</Enabled>
        </SpecialFolderRedirection>
    </ICA_Options>
    <AppAccess>
        <AppAccessMethod>Remote</AppAccessMethod>
        <AppAccessMethod>Streaming</AppAccessMethod>
   </AppAccess>
</PNAgent_Configuration>
